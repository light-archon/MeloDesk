#include "media/player/media_player_catalog_backend.h"

#include "data/data_user.h"
#include "main/main_session.h"
#include "media/audio/media_audio.h"
#include "media/player/media_player_instance.h"
#include "crl/crl_async.h"
#include "crl/crl_on_main.h"

#include <windows.h>

#include <QtCore/QCoreApplication>
#include <QtCore/QFileInfo>
#include <QtCore/QJsonDocument>
#include <QtCore/QBuffer>
#include <QtCore/QPointer>
#include <QtCore/QDir>
#include <QtCore/QStandardPaths>
#include <QtCore/QRegularExpression>
#include <QtGui/QImageReader>
#include <QtNetwork/QNetworkReply>

namespace Media::Player {
namespace {
QPointer<CatalogBackend> ActiveCatalog;
} // namespace

CatalogBackend::CatalogBackend(not_null<Main::Session*> session)
: _session(session) {
	_process.setCreateProcessArgumentsModifier([](
			QProcess::CreateProcessArguments *arguments) {
		arguments->flags |= CREATE_NO_WINDOW;
	});
	QObject::connect(&_process, &QProcess::readyReadStandardOutput, this, [=] {
		read();
	});
	_process.setStandardErrorFile(QProcess::nullDevice());
	QObject::connect(&_process, &QProcess::errorOccurred, this, [=] {
		_ready = false;
		_commands.clear();
		_pending.clear();
		_timer.stop();
		error(u"Music service could not be started."_q);
	});
	QObject::connect(
		&_process,
		qOverload<int, QProcess::ExitStatus>(&QProcess::finished),
		this,
		[=] {
			_ready = false;
			_commands.clear();
			_pending.clear();
			_input.clear();
			_lastPlaybackRequestId = 0;
			++_processGeneration;
			_playback = {};
			_timer.stop();
		});
	_timer.setInterval(500);
	QObject::connect(&_timer, &QTimer::timeout, this, [=] {
		const auto state = _playback.value(u"State"_q).toString();
		if (_ready && (state == u"Playing"_q || state == u"Loading"_q)
			&& !_commands.values().contains(u"state"_q)) {
			send({ { u"command"_q, u"state"_q } });
		}
	});
}

CatalogBackend::~CatalogBackend() {
	_timer.stop();
	for (const auto reply : _network.findChildren<QNetworkReply*>()) {
		QObject::disconnect(reply, nullptr, this, nullptr);
		reply->abort();
	}
	QObject::disconnect(&_process, nullptr, this, nullptr);
	if (_process.state() == QProcess::NotRunning) {
		return;
	}
	_process.closeWriteChannel();
	if (!_process.waitForFinished(25000)) {
		_process.kill();
		_process.waitForFinished(1000);
	}
}

void CatalogBackend::start() {
	const auto path = QCoreApplication::applicationDirPath()
		+ u"/MusicHost/MeloDesk.MusicHost.exe"_q;
	if (!QFileInfo::exists(path)) {
		_pending.clear();
		_commands.clear();
		error(u"The MusicHost folder is missing from this build."_q);
		return;
	}
	_process.setProgram(path);
	_process.setArguments({
		u"--account="_q + QString::number(_session->userId().bare),
		u"--parent-pid="_q + QString::number(QCoreApplication::applicationPid()),
	});
	_process.start();
}

void CatalogBackend::send(QJsonObject request) {
	const auto command = request.value(u"command"_q).toString();
	if (command == u"select"_q) {
		if (ActiveCatalog && ActiveCatalog != this) {
			ActiveCatalog->send({ { u"command"_q, u"stop"_q } });
		}
		ActiveCatalog = this;
		instance()->pause(AudioMsgId::Type::Song);
		instance()->pause(AudioMsgId::Type::Voice);
	}
	if (_pending.size() >= 32 || _commands.size() >= 32) {
		error(u"The music service is busy. Please try again."_q);
		return;
	}
	const auto id = QString::number(++_serial);
	if (command.startsWith(u"import"_q) && command != u"importCancel"_q) _lastImportId = id;
	if (command == u"genre"_q || command == u"genrePage"_q) _lastGenreId = id;
	if (command == u"search"_q) {
		_lastSearchId = id;
		_tracks = {};
	}
	request.insert(u"id"_q, id);
	_commands.insert(id, command);
	_pending.push_back(std::move(request));
	if (_process.state() == QProcess::NotRunning) {
		_ready = false;
		start();
	} else if (_ready) {
		flush();
	}
}

void CatalogBackend::flush() {
	for (const auto &request : base::take(_pending)) {
		const auto written = _process.write(
			QJsonDocument(request).toJson(QJsonDocument::Compact) + '\n');
		if (written < 0) {
			error(u"Could not send a command to the music service."_q);
		}
	}
}

void CatalogBackend::read() {
	_input += _process.readAllStandardOutput();
	if (_input.size() > 16 * 1024 * 1024) {
		_input.clear();
		error(u"Invalid response from the music service."_q);
		return;
	}
	if (_parsing) return;
	auto lines = std::vector<QByteArray>();
	auto bytes = 0;
	while (true) {
		const auto end = _input.indexOf('\n');
		if (end < 0) {
			break;
		}
		const auto line = _input.left(end);
		_input.remove(0, end + 1);
		lines.push_back(line);
		bytes += line.size();
		if (bytes >= 512 * 1024) break;
	}
	if (lines.empty()) return;
	_parsing = true;
	const auto generation = _processGeneration;
	const auto weak = QPointer<CatalogBackend>(this);
	crl::async([weak, generation, lines = std::move(lines)] {
		auto messages = std::vector<QJsonObject>();
		for (const auto &line : lines) {
			const auto document = QJsonDocument::fromJson(line);
			if (document.isObject()) messages.push_back(document.object());
		}
		crl::on_main([weak, generation, messages = std::move(messages)] {
			if (!weak) return;
			weak->_parsing = false;
			if (generation == weak->_processGeneration) {
				for (const auto &message : messages) weak->receive(message);
			}
			weak->read();
		});
	});
}

void CatalogBackend::receive(QJsonObject message) {
	const auto event = message.value(u"Event"_q).toString();
	if (event == u"ready"_q) {
		_ready = message.value(u"Protocol"_q).toInt() == 1;
		if (!_ready) {
			error(u"Unsupported music service version."_q);
			return;
		}
		flush();
		_timer.start();
	} else if (event == u"playback"_q) {
		const auto snapshot = message.value(u"Snapshot"_q).toObject();
		const auto request = snapshot.value(u"RequestId"_q).toInteger();
		if (request < _lastPlaybackRequestId) {
			return;
		}
		_lastPlaybackRequestId = request;
		_playback = snapshot;
		_timer.setInterval(snapshot.value(u"State"_q).toString() == u"Playing"_q ? 500 : 2000);
	}
	const auto id = message.value(u"Id"_q).toString();
	if (event == u"searchBatch"_q) {
		if (id != _lastSearchId) {
			return;
		}
		for (const auto &track : message.value(u"Tracks"_q).toArray()) {
			if (!_tracks.contains(track)) {
				_tracks.push_back(track);
			}
		}
	}
	if (message.contains(u"Ok"_q)) {
		const auto command = _commands.take(id);
		if (command == u"search"_q && id != _lastSearchId) {
			return;
		}
		if ((command == u"genre"_q || command == u"genrePage"_q) && id != _lastGenreId) return;
		if (command.startsWith(u"import"_q) && command != u"importCancel"_q && id != _lastImportId) return;
		message.insert(u"Command"_q, command);
		if (message.value(u"Ok"_q).toBool()) {
			const auto result = message.value(u"Result"_q).toObject();
			if (command == u"state"_q || command == u"controls"_q || command == u"dislike"_q) _controls = result;
			if (command == u"state"_q) {
				const auto snapshot = result.value(u"Playback"_q).toObject();
				const auto request = snapshot.value(u"RequestId"_q).toInteger();
				if (request < _lastPlaybackRequestId) {
					return;
				}
				_lastPlaybackRequestId = request;
				_playback = snapshot;
			} else if (command == u"search"_q) {
				_tracks = result.value(u"Tracks"_q).toArray();
			} else if (command == u"library"_q || command == u"localAdd"_q || command == u"importCommit"_q) {
				if (result.contains(u"FavoriteArtists"_q)) _favoriteArtists = result.value(u"FavoriteArtists"_q).toArray();
				_playlists = result.value(u"Playlists"_q).toArray();
				_likedUrls.clear();
				for (const auto &item : _playlists) {
					const auto playlist = item.toObject();
					if (!playlist.value(u"IsFavorites"_q).toBool()) continue;
					for (const auto &track : playlist.value(u"Tracks"_q).toArray()) _likedUrls.insert(track.toObject().value(u"Url"_q).toString());
				}
			} else if (command == u"favoriteArtist"_q) {
				_favoriteArtists = result.value(u"FavoriteArtists"_q).toArray();
			} else if (command == u"playlistChange"_q || command == u"like"_q) {
				applyLibraryChange(result.value(u"Change"_q).toObject());
			}
		}
	}
	_messages.fire(std::move(message));
}

void CatalogBackend::applyLibraryChange(const QJsonObject &change) {
	const auto id = change.value(u"PlaylistId"_q).toString();
	const auto kind = change.value(u"Kind"_q).toString();
	auto index = -1;
	for (auto i = 0; i != _playlists.size(); ++i) {
		if (_playlists[i].toObject().value(u"Id"_q).toString() == id) { index = i; break; }
	}
	if (kind == u"delete"_q) {
		if (index >= 0) _playlists.removeAt(index);
		return;
	}
	if (index < 0 && kind != u"create"_q) return;
	auto playlist = index >= 0 ? _playlists[index].toObject() : QJsonObject{
		{ u"Id"_q, id }, { u"Name"_q, change.value(u"Name"_q) }, { u"IsFavorites"_q, false }, { u"Tracks"_q, QJsonArray() } };
	if (kind == u"rename"_q) playlist.insert(u"Name"_q, change.value(u"Name"_q));
	auto tracks = playlist.value(u"Tracks"_q).toArray();
	const auto track = change.value(u"Track"_q).toObject();
	const auto url = kind == u"remove"_q ? change.value(u"TrackUrl"_q).toString() : track.value(u"Url"_q).toString();
	if (kind == u"remove"_q) {
		for (auto i = tracks.size(); i > 0; --i) if (tracks[i - 1].toObject().value(u"Url"_q).toString() == url) tracks.removeAt(i - 1);
	} else if ((kind == u"add"_q || kind == u"create"_q) && !track.isEmpty()) {
		const auto exists = ranges::any_of(tracks, [&](const auto &item) { return item.toObject().value(u"Url"_q).toString() == url; });
		if (!exists) tracks.push_back(track);
	}
	playlist.insert(u"Tracks"_q, tracks);
	if (playlist.value(u"IsFavorites"_q).toBool()) {
		if (kind == u"remove"_q) _likedUrls.remove(url); else if (!url.isEmpty()) _likedUrls.insert(url);
	}
	if (index >= 0) _playlists[index] = playlist; else _playlists.push_back(playlist);
}

bool CatalogBackend::isLiked(const QString &url) const {
	return _likedUrls.contains(url);
}

QJsonArray CatalogBackend::favoriteArtists() const {
	return _favoriteArtists;
}

bool CatalogBackend::isFavoriteArtist(const QString &id) const {
	return ranges::any_of(_favoriteArtists, [&](const auto &value) {
		return value.toObject().value(u"Id"_q).toString() == id;
	});
}

void CatalogBackend::error(const QString &text) {
	_messages.fire({ { u"Event"_q, u"error"_q }, { u"Error"_q, text } });
}

rpl::producer<QJsonObject> CatalogBackend::messages() const {
	return _messages.events();
}

QJsonArray CatalogBackend::tracks() const {
	return _tracks;
}

QJsonArray CatalogBackend::playlists() const {
	return _playlists;
}

QJsonObject CatalogBackend::playback() const {
	return _playback;
}

QJsonObject CatalogBackend::controls() const {
	return _controls;
}

void CatalogBackend::registerSection(bool opened) {
	_sections = std::max(0, _sections + (opened ? 1 : -1));
	_messages.fire({ { u"Event"_q, u"section"_q }, { u"Visible"_q, _sections > 0 } });
}

bool CatalogBackend::sectionVisible() const {
	return _sections > 0;
}

QImage CatalogBackend::cover(const QString &url) {
	if (const auto image = _covers.object(url)) {
		return *image;
	}
	const auto parsed = QUrl(url);
	if (parsed.isLocalFile() && !_coverJobs.contains(url) && _coverJobs.size() < 4) {
		const auto path = parsed.toLocalFile();
		const auto root = QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation)
			+ u"/MusiGram/accounts/"_q + QString::number(_session->userId().bare) + u"/audio"_q;
		if (!path.startsWith(root + u'/', Qt::CaseInsensitive)
			|| !QRegularExpression(u"^[a-f0-9]{64}\\.cover\\.(png|jpg)$"_q).match(QFileInfo(path).fileName()).hasMatch()) return {};
		_coverJobs.insert(url);
		const auto weak = QPointer<CatalogBackend>(this);
		crl::async([weak, path, root, url] {
			auto decoded = QImage();
			const auto file = QFileInfo(path);
			if (file.size() <= 1024 * 1024 && file.canonicalPath().compare(QFileInfo(root).canonicalFilePath(), Qt::CaseInsensitive) == 0) {
				auto reader = QImageReader(path);
				reader.setScaledSize(QSize(128, 128));
				decoded = reader.read();
			}
			crl::on_main([weak, decoded, url] {
				if (!weak) return;
				weak->_coverJobs.remove(url);
				weak->_covers.insert(url, new QImage(decoded));
				weak->_messages.fire({ { u"Event"_q, u"artwork"_q }, { u"Url"_q, url } });
			});
		});
		return {};
	}
	if (url.isEmpty() || _coverJobs.contains(url)
		|| (parsed.scheme() != u"https"_q && parsed.scheme() != u"http"_q)
		|| _coverJobs.size() >= 4) {
		return {};
	}
	_coverJobs.insert(url);
	auto request = QNetworkRequest(parsed);
	request.setTransferTimeout(5000);
	const auto reply = _network.get(request);
	QObject::connect(reply, &QNetworkReply::readyRead, this, [=] {
		if (reply->bytesAvailable() > 1024 * 1024) {
			reply->abort();
		}
	});
	QObject::connect(reply, &QNetworkReply::finished, this, [=] {
		const auto bytes = reply->readAll();
		if (reply->error() == QNetworkReply::NoError && bytes.size() <= 1024 * 1024) {
			const auto weak = QPointer<CatalogBackend>(this);
			crl::async([weak, bytes, url] {
			auto buffer = QBuffer();
			buffer.setData(bytes);
			buffer.open(QIODevice::ReadOnly);
			auto reader = QImageReader(&buffer);
			reader.setScaledSize(QSize(128, 128));
			const auto decoded = reader.read();
			crl::on_main([weak, decoded, url] {
				if (!weak) return;
				weak->_coverJobs.remove(url);
				weak->_covers.insert(url, new QImage(decoded));
				weak->_messages.fire({ { u"Event"_q, u"artwork"_q }, { u"Url"_q, url } });
			});
			});
		} else {
			_coverJobs.remove(url);
			_covers.insert(url, new QImage());
		}
		reply->deleteLater();
	});
	return {};
}

} // namespace Media::Player
