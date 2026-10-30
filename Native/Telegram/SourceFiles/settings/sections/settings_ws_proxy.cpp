/*
MeloDesk integration for Telegram Desktop.
Distributed under the Telegram Desktop GPL license; see LEGAL.
TG WS Proxy core is distributed separately under its MIT license.
*/
#include "settings/sections/settings_ws_proxy.h"

#include "core/application.h"
#include "core/core_settings.h"
#include "main/main_account.h"
#include "mtproto/proxy_check.h"
#include "storage/localstorage.h"
#include "ui/layers/generic_box.h"
#include "ui/widgets/checkbox.h"
#include "ui/widgets/fields/input_field.h"
#include "ui/widgets/labels.h"
#include "styles/style_boxes.h"
#include "styles/style_layers.h"
#include "styles/style_widgets.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QCryptographicHash>
#include <QtCore/QFileInfo>
#include <QtCore/QJsonDocument>
#include <QtCore/QJsonObject>
#include <QtCore/QProcess>
#include <QtCore/QSettings>
#include <QtCore/QTimer>
#include <QtCore/QUuid>
#include <QtNetwork/QHostAddress>
#include <QtNetwork/QTcpServer>

#ifdef Q_OS_WIN
#include <windows.h>
#endif

namespace Settings {
namespace {

struct WsConfiguration {
	int port = 22443;
	QString secret;
	QString dc2 = u"149.154.167.220"_q;
	QString dc4 = u"149.154.167.220"_q;
	bool fallback = true;
	bool operator==(const WsConfiguration &) const = default;
};

QSettings WsSettings() {
	return QSettings(u"MusiGram"_q, u"TgWsProxy"_q);
}

WsConfiguration ReadWsConfiguration() {
	auto settings = WsSettings();
	auto result = WsConfiguration();
	result.port = settings.value(u"port"_q, 22443).toInt();
	result.dc2 = settings.value(u"dc2"_q, result.dc2).toString();
	result.dc4 = settings.value(u"dc4"_q, result.dc4).toString();
	result.fallback = settings.value(u"fallback"_q, true).toBool();
	result.secret = settings.value(u"secret"_q).toString();
	if (result.secret.size() != 32
		|| QByteArray::fromHex(result.secret.toLatin1()).size() != 16) {
		result.secret = QUuid::createUuid().toString(QUuid::Id128);
		settings.setValue(u"secret"_q, result.secret);
	}
	return result;
}

MTP::ProxyData WsProxy(const WsConfiguration &config) {
	auto proxy = MTP::ProxyData();
	proxy.type = MTP::ProxyData::Type::Mtproto;
	proxy.host = u"127.0.0.1"_q;
	proxy.port = config.port;
	proxy.password = u"dd"_q + config.secret;
	return proxy;
}

QByteArray ProxyFingerprint(const MTP::ProxyData &proxy) {
	const auto data = QJsonObject{
		{ u"type"_q, int(proxy.type) }, { u"host"_q, proxy.host },
		{ u"port"_q, int(proxy.port) }, { u"user"_q, proxy.user },
		{ u"password"_q, proxy.password },
	};
	return QCryptographicHash::hash(
		QJsonDocument(data).toJson(QJsonDocument::Compact),
		QCryptographicHash::Sha256).toHex();
}

class WsProxyService final : public QObject {
public:
	WsProxyService() : QObject(QCoreApplication::instance()) {
		_process.setProcessChannelMode(QProcess::SeparateChannels);
#ifdef Q_OS_WIN
		_process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments *args) {
			args->flags |= CREATE_NO_WINDOW;
		});
#endif
		QObject::connect(&_process, &QProcess::started, this, [=] {
			const auto config = QJsonObject{
				{ u"port"_q, _config.port }, { u"secret"_q, _config.secret },
				{ u"fallback"_q, _config.fallback },
				{ u"dc"_q, QJsonObject{ { u"2"_q, _config.dc2 }, { u"4"_q, _config.dc4 } } },
			};
			_process.write(QJsonDocument(config).toJson(QJsonDocument::Compact) + '\n');
		});
		QObject::connect(&_process, &QProcess::readyReadStandardOutput, this, [=] {
			_output += _process.readAllStandardOutput();
			if (_output.size() > 65536) { stop(); return; }
			while (_output.contains('\n')) {
				const auto end = _output.indexOf('\n');
				const auto line = _output.left(end);
				_output.remove(0, end + 1);
				const auto event = QJsonDocument::fromJson(line).object().value(u"event"_q).toString();
				if (event == u"ready"_q) {
					_ready = true;
					_startTimeout.stop();
					_status = u"Запущен · 127.0.0.1:"_q + QString::number(_config.port);
				} else if (event == u"error"_q) {
					_status = u"Не удалось запустить TgWsProxy. Проверьте порт и адреса DC."_q;
				}
			}
		});
		QObject::connect(&_process, &QProcess::readyReadStandardError, this, [=] {
			_process.readAllStandardError();
		});
		QObject::connect(&_process, &QProcess::errorOccurred, this, [=](QProcess::ProcessError error) {
			if (error == QProcess::FailedToStart) {
				_startTimeout.stop();
				_status = u"Компонент TgWsProxy недоступен. Переустановите MeloDesk."_q;
				if (!_quitting) restore();
			}
		});
		QObject::connect(&_process, qOverload<int, QProcess::ExitStatus>(&QProcess::finished), this, [=] {
			_startTimeout.stop();
			_ready = false;
			if (!_stopping) _status = u"TgWsProxy завершился. Предыдущее подключение восстановлено."_q;
			if (!_quitting) restore();
		});
		_startTimeout.setSingleShot(true);
		QObject::connect(&_startTimeout, &QTimer::timeout, this, [=] {
			stop();
			_status = u"TgWsProxy не успел запуститься. Подключение не изменено."_q;
		});
		QObject::connect(QCoreApplication::instance(), &QCoreApplication::aboutToQuit, this, [=] {
			_quitting = true;
			_process.write("stop\n");
			_process.closeWriteChannel();
		});
	}
	~WsProxyService() {
		_quitting = true;
		if (running() && !_process.waitForFinished(1200)) {
			_process.kill();
			_process.waitForFinished(500);
		}
	}
	bool ready() const { return _ready; }
	bool running() const { return _process.state() != QProcess::NotRunning; }
	QString status() const { return _status; }
	bool start(const WsConfiguration &config) {
		if (running()) {
			if (_stopping) {
				_status = u"TgWsProxy завершает работу. Повторите через несколько секунд."_q;
				return false;
			}
			if (_config == config) return true;
			_status = u"Сначала отключите TgWsProxy, затем измените настройки."_q;
			return false;
		}
		_config = config;
		if (config.port < 1024 || config.port > 65535
			|| (!config.dc2.isEmpty() && QHostAddress(config.dc2).isNull())
			|| (!config.dc4.isEmpty() && QHostAddress(config.dc4).isNull())) {
			_status = u"Укажите порт 1024–65535 и IP-адреса DC. Пустой адрес отключает перенаправление."_q;
			return false;
		}
		auto listener = QTcpServer();
		if (!listener.listen(QHostAddress::LocalHost, config.port)) {
			_status = u"Этот порт занят. Выберите другой порт."_q;
			return false;
		}
		listener.close();
		const auto root = QCoreApplication::applicationDirPath() + u"/Proxy"_q;
		if (!QFileInfo::exists(root + u"/runtime/python.exe"_q)
			|| !QFileInfo::exists(root + u"/runner.py"_q)) {
			_status = u"Компонент TgWsProxy не установлен."_q;
			return false;
		}
		_stopping = _ready = false;
		_output.clear();
		_status = u"Запуск TgWsProxy…"_q;
		_process.setWorkingDirectory(root);
		_process.start(root + u"/runtime/python.exe"_q, { u"-I"_q, u"-B"_q, root + u"/runner.py"_q });
		_startTimeout.start(12000);
		return true;
	}
	void activate(const WsConfiguration &config, bool autoStart) {
		auto settings = WsSettings();
		auto &proxies = Core::App().settings().proxy();
		const auto proxy = WsProxy(config);
		if (proxies.selected() != proxy || !proxies.isEnabled()) {
			settings.setValue(u"previousMode"_q, int(proxies.settings()));
			settings.setValue(u"previousFingerprint"_q, ProxyFingerprint(proxies.selected()));
		}
		settings.setValue(u"port"_q, config.port);
		settings.setValue(u"dc2"_q, config.dc2);
		settings.setValue(u"dc4"_q, config.dc4);
		settings.setValue(u"fallback"_q, config.fallback);
		settings.setValue(u"autoStart"_q, autoStart);
		if (proxies.indexInList(proxy) < 0) proxies.addToList(proxy);
		Core::App().setCurrentProxy(proxy, MTP::ProxyData::Settings::Enabled);
		Local::writeSettings();
	}
	void restoreStoredConfiguration(const WsConfiguration &config) {
		_config = config;
		restore();
	}
	void stop() {
		restore();
		WsSettings().setValue(u"autoStart"_q, false);
		_stopping = true;
		_ready = false;
		_status = u"TgWsProxy отключён."_q;
		_startTimeout.stop();
		const auto pid = _process.processId();
		if (!running()) return;
		_process.write("stop\n");
		_process.closeWriteChannel();
		QTimer::singleShot(2000, this, [=] {
			if (running() && _process.processId() == pid) _process.kill();
		});
	}
private:
	void restore() {
		auto &proxies = Core::App().settings().proxy();
		if (!proxies.isEnabled() || proxies.selected() != WsProxy(_config)) return;
		auto settings = WsSettings();
		auto mode = MTP::ProxyData::Settings(settings.value(u"previousMode"_q, 0).toInt());
		auto previous = MTP::ProxyData();
		for (const auto &item : proxies.list()) {
			if (ProxyFingerprint(item) == settings.value(u"previousFingerprint"_q).toByteArray()) {
				previous = item;
				break;
			}
		}
		if (mode == MTP::ProxyData::Settings::Enabled && !previous.valid()) mode = MTP::ProxyData::Settings::System;
		Core::App().setCurrentProxy(previous, mode);
		Local::writeSettings();
	}
	WsConfiguration _config;
	QProcess _process;
	QTimer _startTimeout;
	QByteArray _output;
	QString _status = u"TgWsProxy выключен."_q;
	bool _ready = false;
	bool _stopping = false;
	bool _quitting = false;
};

WsProxyService &WsService() {
	static const auto service = new WsProxyService();
	return *service;
}

} // namespace

void StartBuiltInWsProxyIfEnabled() {
	static auto started = false;
	if (started) return;
	const auto settings = WsSettings();
	if (!settings.contains(u"secret"_q)) return;
	const auto config = ReadWsConfiguration();
	if (Core::App().settings().proxy().isEnabled()
		&& Core::App().settings().proxy().selected() == WsProxy(config)) {
		started = true;
		if (settings.value(u"autoStart"_q, false).toBool()) {
			if (!WsService().start(config)) WsService().stop();
		} else {
			WsService().restoreStoredConfiguration(config);
		}
	}
}

object_ptr<Ui::BoxContent> BuiltInWsProxyBox(not_null<Main::Account*> account) {
	const auto weakAccount = base::make_weak(account.get());
	return Box([=](not_null<Ui::GenericBox*> box) {
		box->setTitle(rpl::single(u"TgWsProxy By FlowSeal"_q));
		box->setWidth(st::boxWideWidth + st::boxWidth / 2);
		const auto config = ReadWsConfiguration();
		box->addRow(object_ptr<Ui::FlatLabel>(box, rpl::single(
			u"Соединение Telegram через WebSocket. Работает внутри MeloDesk. Музыка YouTube и SoundCloud подключается отдельно."_q), st::defaultFlatLabel));
		box->addSkip(st::boxLittleSkip);
		const auto port = box->addRow(object_ptr<Ui::InputField>(box, st::defaultInputField, rpl::single(u"Локальный порт"_q), QString::number(config.port)));
		box->addSkip(st::boxLittleSkip);
		const auto dc2 = box->addRow(object_ptr<Ui::InputField>(box, st::defaultInputField, rpl::single(u"IP для DC2 (можно оставить пустым)"_q), config.dc2));
		box->addSkip(st::boxLittleSkip);
		const auto dc4 = box->addRow(object_ptr<Ui::InputField>(box, st::defaultInputField, rpl::single(u"IP для DC4 (можно оставить пустым)"_q), config.dc4));
		box->addSkip(st::boxLittleSkip);
		const auto fallback = box->addRow(object_ptr<Ui::Checkbox>(box, rpl::single(u"Резервное подключение CF proxy"_q), config.fallback, st::defaultCheckbox));
		box->addSkip(st::boxLittleSkip);
		const auto autoStart = box->addRow(object_ptr<Ui::Checkbox>(box, rpl::single(u"Запускать вместе с MeloDesk"_q), WsSettings().value(u"autoStart"_q, true).toBool(), st::defaultCheckbox));
		box->addSkip(st::boxLittleSkip);
		struct State {
			~State() {
				MTP::ResetProxyCheckers(v4, v6);
				if (checking && !wasRunning) WsService().stop();
			}
			rpl::variable<QString> message = WsService().status();
			MTP::ProxyCheckConnection v4, v6;
			QTimer timer;
			bool checking = false;
			bool connected = false;
			bool wasRunning = false;
			int elapsed = 0;
			uint64 generation = 0;
			WsConfiguration requested;
		};
		const auto state = box->lifetime().make_state<State>();
		box->addRow(object_ptr<Ui::FlatLabel>(box, state->message.value(), st::defaultFlatLabel));
		box->addSkip(st::boxLittleSkip);
		const auto fail = [=](QString message) {
			state->checking = false;
			state->timer.stop();
			MTP::ResetProxyCheckers(state->v4, state->v6);
			if (!state->wasRunning) WsService().stop();
			state->message = std::move(message);
		};
		QObject::connect(&state->timer, &QTimer::timeout, box, [=] {
			state->elapsed += 150;
			if (!state->checking) return;
			if (state->elapsed > 30000 || !WsService().running()) {
				fail(u"Соединение не подтверждено. Прежний прокси сохранён. Проверьте сеть и настройки DC."_q);
				return;
			}
			if (!WsService().ready() || state->connected) return;
			state->connected = true;
			const auto generation = state->generation;
			state->message = u"Проверяем подключение к Telegram…"_q;
			if (!weakAccount) {
				fail(u"Экран входа закрыт. Откройте TgWsProxy повторно."_q);
				return;
			}
			MTP::StartProxyCheck(&weakAccount->mtp(), WsProxy(state->requested), false, state->v4, state->v6,
				[=](MTP::details::AbstractConnection *, int ping) {
					QTimer::singleShot(0, box, [=] {
						if (!state->checking || state->generation != generation) return;
						state->checking = false;
						state->timer.stop();
						MTP::ResetProxyCheckers(state->v4, state->v6);
						WsService().activate(state->requested, autoStart->checked());
						state->message = u"Подключено через TgWsProxy · "_q + QString::number(ping) + u" мс"_q;
					});
				}, [=](MTP::details::AbstractConnection *) {
					QTimer::singleShot(0, box, [=] {
						if (state->checking && state->generation == generation) fail(u"Telegram не ответил через TgWsProxy. Прежнее подключение сохранено."_q);
					});
				});
		});
		box->addButton(rpl::single(u"Проверить и включить"_q), [=] {
			if (state->checking) return;
			++state->generation;
			state->requested = config;
			state->requested.port = port->getLastText().trimmed().toInt();
			state->requested.dc2 = dc2->getLastText().trimmed();
			state->requested.dc4 = dc4->getLastText().trimmed();
			state->requested.fallback = fallback->checked();
			state->wasRunning = WsService().running();
			if (!WsService().start(state->requested)) {
				state->message = WsService().status();
				return;
			}
			state->checking = true;
			state->connected = false;
			state->elapsed = 0;
			state->message = u"Запуск и проверка подключения…"_q;
			state->timer.start(150);
		});
		box->addLeftButton(rpl::single(u"Отключить"_q), [=] {
			++state->generation;
			state->checking = false;
			state->timer.stop();
			MTP::ResetProxyCheckers(state->v4, state->v6);
			WsService().stop();
			state->message = WsService().status();
		});
		box->addButton(rpl::single(u"Закрыть"_q), [=] { box->closeBox(); });
	});
}

} // namespace Settings
