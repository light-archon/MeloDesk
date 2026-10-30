#include "info/saved/info_saved_music_catalog.h"

#include "base/event_filter.h"
#include "base/object_ptr.h"
#include "base/timer.h"
#include "base/unique_qptr.h"
#include "core/file_utilities.h"
#include "core/click_handler_types.h"
#include "crl/crl_async.h"
#include "crl/crl_on_main.h"
#include <QtCore/QCoreApplication>
#include <QtCore/QFile>
#include <QtCore/QJsonDocument>
#include <QtCore/QSettings>
#include <QtCore/QRegularExpression>
#include <QtCore/QTimer>
#include <QtCore/QUuid>
#include <QtGui/QImageReader>
#include <QtCore/QBuffer>
#include <QtNetwork/QNetworkAccessManager>
#include <QtNetwork/QNetworkReply>

#include "data/data_user.h"
#include "lang/lang_keys.h"
#include "main/main_session.h"
#include "media/player/media_player_button.h"
#include "media/player/media_player_catalog_backend.h"
#include "ui/boxes/confirm_box.h"
#include "ui/basic_click_handlers.h"
#include "ui/ui_utility.h"
#include "ui/text/text_utilities.h"
#include "ui/layers/generic_box.h"
#include "ui/widgets/buttons.h"
#include "ui/widgets/continuous_sliders.h"
#include "ui/widgets/fields/input_field.h"
#include "ui/widgets/labels.h"
#include "ui/widgets/popup_menu.h"
#include "ui/widgets/scroll_area.h"
#include "ui/wrap/vertical_layout.h"
#include "window/section_widget.h"
#include "window/main_window.h"
#include "window/section_memento.h"
#include "window/window_session_controller.h"

#include "styles/style_info.h"
#include "styles/style_layers.h"
#include "styles/style_media_player.h"
#include "styles/style_musigram_music.h"
#include "styles/style_widgets.h"

#include <QtGui/QtEvents>
#include <QtCore/QUuid>
#include <QtCore/QPointer>
#include <QtGui/QFontMetrics>
#include <QtGui/QLinearGradient>
#include <QtGui/QRadialGradient>
#include <QtGui/QPainterPath>
#include <QtWidgets/QApplication>
#include <cmath>
#include <algorithm>

namespace Info::Saved {
namespace {

void DrawSquareCover(QPainter &p, QRect target, const QImage &image) {
	const auto side = std::min(image.width(), image.height());
	p.drawImage(target, image, QRect((image.width() - side) / 2, (image.height() - side) / 2, side, side));
}

void DrawBrandLogo(QPainter &p, QRect target) {
	const auto center = target.center();
	const auto side = std::min(target.width(), target.height());
	target.setSize(QSize(side, side));
	target.moveCenter(center);
	DrawSquareCover(p, target, Window::LogoNoMargin());
}

void DrawHeart(QPainter &p, QRectF target, QColor stroke, QColor fill) {
	p.save();
	p.translate(target.topLeft());
	p.scale(target.width() / 24., target.height() / 24.);
	auto heart = QPainterPath();
	heart.moveTo(12, 21);
	heart.cubicTo(9, 18, 2, 13, 2, 7.5);
	heart.cubicTo(2, 1.5, 9, .5, 12, 5.5);
	heart.cubicTo(15, .5, 22, 1.5, 22, 7.5);
	heart.cubicTo(22, 13, 15, 18, 12, 21);
	p.setPen(QPen(stroke, 1.6, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
	p.setBrush(fill);
	p.drawPath(heart);
	p.restore();
}


class PromotionBanner final : public Ui::AbstractButton {
public:
	PromotionBanner(QWidget *parent, Fn<void()> changed)
	: AbstractButton(parent), _bot(this, rpl::single(QString()), st::defaultLightButton), _changed(std::move(changed)) {
		setPointerCursor(true);
		setVisible(false);
		setClickedCallback([=] { if (!_url.isEmpty()) UrlClickHandler::Open(_url); });
		_bot.setClickedCallback([=] { if (!_botUrl.isEmpty()) UrlClickHandler::Open(_botUrl); });

		_timer.setInterval(15000);
		QObject::connect(&_timer, &QTimer::timeout, this, [=] { refresh(); });
		QTimer::singleShot(0, this, [=] { refresh(); });
	}
	~PromotionBanner() override {
		_timer.stop();
		for (const auto reply : _network.findChildren<QNetworkReply*>()) {
			QObject::disconnect(reply, nullptr, this, nullptr);
			reply->abort();
		}
	}
	bool available() const { return _available; }
	QString accessibilityName() override { return accessibleName(); }
protected:
	int resizeGetHeight(int) override { return _available ? 184 : 0; }
	void resizeEvent(QResizeEvent *event) override {
		Ui::AbstractButton::resizeEvent(event);
		_bot.resize(130, 32); _bot.move(width() - 150, height() - 44);
		_bot.setVisible(_showBot && width() >= 340);
	}
	void paintEvent(QPaintEvent *) override {
		auto p = QPainter(this);
		p.setRenderHint(QPainter::Antialiasing);
		auto gradient = QLinearGradient(0, 0, width(), height());
		gradient.setColorAt(0, st::windowBgOver->c);
		gradient.setColorAt(1, st::dialogsBgActive->c);
		p.setPen(Qt::NoPen); p.setBrush(gradient); p.drawRoundedRect(rect(), 14, 14);
		const auto art = width() >= 560;
		const auto compact = !art && width() >= 300;
		const auto left = art ? 170 : compact ? 80 : 20;
		if (art) {
			const auto target = QRect(16, 16, 136, height() - 32);
			if (!_image.isNull()) {
				p.save(); auto clip = QPainterPath(); clip.addRoundedRect(target, 12, 12); p.setClipPath(clip);
				DrawSquareCover(p, target, _image); p.restore();
			} else {
				DrawBrandLogo(p, target);
			}
		}
		if (compact) {
			const auto target = QRect(20, 42, 48, 48);
			if (!_image.isNull()) {
				p.save(); auto clip = QPainterPath(); clip.addRoundedRect(target, 10, 10); p.setClipPath(clip);
				DrawSquareCover(p, target, _image); p.restore();
			} else {
				DrawBrandLogo(p, target);
			}
		}
		const auto textWidth = std::max(0, width() - left - 48);
		p.setPen(st::windowSubTextFg->c); p.setFont(st::normalFont->f);
		p.drawText(QRect(art ? left : 20, 14, textWidth, 20), Qt::AlignLeft | Qt::AlignVCenter, tr::lng_sponsored_message_title(tr::now) + u" · MeloDesk"_q);
		p.setPen(st::windowFg->c); p.setFont(st::musigramCatalogHeading->f);
		p.drawText(QRect(left, 38, textWidth, 28), Qt::AlignLeft | Qt::AlignVCenter, QFontMetrics(p.font()).elidedText(_title, Qt::ElideRight, textWidth));
		p.setFont(st::normalFont->f); p.setPen(st::windowSubTextFg->c);
		p.drawText(QRect(left, 72, textWidth, 42), Qt::AlignLeft | Qt::AlignTop | Qt::TextWordWrap, _text);
		p.setPen(_url.isEmpty() ? st::windowSubTextFg->c : st::windowActiveTextFg->c); p.setFont(st::musigramCatalogTrackFont->f);
		const auto buttonLeft = art ? left : 20;
		const auto buttonWidth = std::max(0, width() - buttonLeft - (_bot.isHidden() ? 20 : 164));
		p.drawText(QRect(buttonLeft, height() - 42, buttonWidth, 28), Qt::AlignLeft | Qt::AlignVCenter, QFontMetrics(p.font()).elidedText(_button + (_url.isEmpty() ? QString() : u" →"_q), Qt::ElideRight, buttonWidth));
	}
private:
	static bool telegramLink(const QUrl &url) {
		return url.scheme() == u"https"_q && url.host() == u"t.me"_q
			&& url.userInfo().isEmpty() && url.port() == -1
			&& url.path().size() > 1 && url.query().isEmpty() && url.fragment().isEmpty();
	}
	void showEmbedded() {
		const auto revision = u"melodesk-embedded-house-v1"_q;
		if (_available && _revision == revision) return;
		_title = u"Подпишись на нас MeloDesk News"_q; _text = u"Новости MeloDesk, обновления и музыка. Всё о проекте — в нашем канале."_q;
		_url = u"https://t.me/MeloDesk"_q; _button = u"Читать новости"_q;
		_botUrl = u"https://t.me/MeloDesk"_q;
		_showBot = !_botUrl.isEmpty();
		_bot.setText(rpl::single(u"Наш канал"_q));
		_bot.setDisabled(_botUrl.isEmpty());
		_bot.setVisible(_showBot && width() >= 340);
		setPointerCursor(!_url.isEmpty());
		setAccessibleName(tr::lng_sponsored_message_title(tr::now) + u" · MeloDesk: "_q + _title);
		_revision = revision; _available = true; _image = QImage();
		_changed(); update();
	}
	void clear() { if (_available) { _available = false; _changed(); } }
	void refresh() {
		if (_fetching) return;
		auto config = QFile(QCoreApplication::applicationDirPath() + u"/promotions.json"_q);
		if (!config.open(QIODevice::ReadOnly) || config.size() > 4096) { _timer.stop(); showEmbedded(); return; }
		const auto settings = QJsonDocument::fromJson(config.readAll()).object();
		if (settings.value(u"Mode"_q).toString() == u"Embedded"_q || settings.value(u"FeedUrl"_q).toString().isEmpty()) {
			_timer.stop(); showEmbedded(); return;
		}
		if (_revision.isEmpty()) showEmbedded();
		if (!_timer.isActive()) _timer.start();
		const auto feed = QUrl(settings.value(u"FeedUrl"_q).toString());
		if (!feed.isValid() || !feed.userInfo().isEmpty()
			|| (feed.scheme() != u"https"_q && !(feed.scheme() == u"http"_q && feed.host() == u"127.0.0.1"_q))) { showEmbedded(); return; }
		_fetching = true;
		auto request = QNetworkRequest(feed); request.setTransferTimeout(5000);
		request.setAttribute(QNetworkRequest::RedirectPolicyAttribute, QNetworkRequest::ManualRedirectPolicy);
		const auto reply = _network.get(request);
		QObject::connect(reply, &QNetworkReply::downloadProgress, this, [=](qint64 received, qint64) { if (received > 32768) reply->abort(); });
		QObject::connect(reply, &QNetworkReply::finished, this, [=] {
			_fetching = false; const auto data = reply->readAll(); const auto ok = reply->error() == QNetworkReply::NoError;
			reply->deleteLater();
			const auto value = QJsonDocument::fromJson(data).object();
			const auto url = QUrl(value.value(u"Url"_q).toString());
			const auto revision = value.value(u"Revision"_q).toString();
			const auto upcoming = value.value(u"Upcoming"_q).toBool();
			if (!ok || data.size() > 32768 || value.value(u"Version"_q).toInt() != 1) { showEmbedded(); return; }
			if (!value.value(u"Enabled"_q).toBool()) { clear(); return; }
			if ((!telegramLink(url) && !(upcoming && url.isEmpty())) || revision.isEmpty() || revision.size() > 128) { showEmbedded(); return; }
			_title = value.value(u"Title"_q).toString().left(80); _text = value.value(u"Text"_q).toString().left(240);
			_button = value.value(u"Button"_q).toString().left(32); _url = telegramLink(url) ? url.toString() : QString();
			setPointerCursor(!_url.isEmpty());
			const auto bot = QUrl(value.value(u"SecondaryUrl"_q).toString());
			_botUrl = telegramLink(bot) ? bot.toString() : QString();
			_showBot = upcoming || !_botUrl.isEmpty();
			_bot.setText(rpl::single(value.value(u"SecondaryButton"_q).toString().left(32)));
			_bot.setDisabled(_botUrl.isEmpty());
			_bot.setVisible(_showBot && width() >= 340);
			setAccessibleName(tr::lng_sponsored_message_title(tr::now) + u" · MeloDesk: "_q + _title);
			const auto changed = revision != _revision; _revision = revision; _available = true;
			if (changed) _image = QImage();
			if (_image.isNull()) loadImage(feed, value.value(u"Image"_q).toString(), revision);
			_changed(); update();
		});
	}
	void loadImage(const QUrl &feed, const QString &path, const QString &revision) {
		if (!path.startsWith(u"/assets/"_q) || path.size() != 72 || path.contains(u".."_q)) return;
		const auto url = feed.resolved(QUrl(path));
		if (url.host() != feed.host() || url.port() != feed.port() || url.scheme() != feed.scheme()) return;
		auto request = QNetworkRequest(url); request.setTransferTimeout(5000);
		request.setAttribute(QNetworkRequest::RedirectPolicyAttribute, QNetworkRequest::ManualRedirectPolicy);
		const auto reply = _network.get(request);
		QObject::connect(reply, &QNetworkReply::downloadProgress, this, [=](qint64 received, qint64) { if (received > 2 * 1024 * 1024) reply->abort(); });
		QObject::connect(reply, &QNetworkReply::finished, this, [=] {
			auto bytes = reply->readAll(); const auto ok = reply->error() == QNetworkReply::NoError; reply->deleteLater();
			if (!ok || revision != _revision || bytes.size() > 2 * 1024 * 1024) return;
			const auto weak = QPointer<PromotionBanner>(this);
			crl::async([weak, bytes = std::move(bytes), revision]() mutable {
				auto buffer = QBuffer(&bytes); buffer.open(QIODevice::ReadOnly);
				auto reader = QImageReader(&buffer);
				const auto size = reader.size();
				if (!size.isValid() || qint64(size.width()) * size.height() > 16000000) return;
				reader.setScaledSize(size.scaled(1200, 600, Qt::KeepAspectRatio));
				auto image = reader.read();
				crl::on_main([weak, revision, image = std::move(image)]() mutable {
					if (!weak || !weak->_available || weak->_revision != revision) return;
					weak->_image = std::move(image); weak->update();
				});
			});
		});
	}
	Ui::RoundButton _bot;
	Fn<void()> _changed;
	QNetworkAccessManager _network;
	QTimer _timer;
	QImage _image;
	QString _title, _text, _url, _button, _botUrl, _revision;
	bool _showBot = false;
	bool _available = false;
	bool _fetching = false;

};


class HeartButton final : public Ui::AbstractButton {
public:
	explicit HeartButton(QWidget *parent);
	void setActive(bool active);
	QString accessibilityName() override;
protected:
	void paintEvent(QPaintEvent *event) override;
	void onStateChanged(State was, StateChangeSource source) override;
private:
	Ui::Animations::Simple _fill;
	Ui::Animations::Simple _hover;
	bool _active = false;

};

HeartButton::HeartButton(QWidget *parent) : AbstractButton(parent) {
	resize(st::musigramCatalogActionWidth, st::musigramCatalogActionWidth);
	setPointerCursor(true);
}

QString HeartButton::accessibilityName() {
	return accessibleName();
}

void HeartButton::setActive(bool active) {
	if (_active == active) return;
	const auto from = _fill.value(_active ? 1. : 0.);
	_active = active;
	_fill.start([=] { update(); }, from, active ? 1. : 0., st::musigramCatalogAnimationDuration, anim::easeOutCirc);
}

void HeartButton::onStateChanged(State, StateChangeSource) {
	_hover.start([=] { update(); }, _hover.value(isOver() ? 0. : 1.), isOver() ? 1. : 0., st::musigramCatalogAnimationDuration, anim::easeOutCirc);
}

void HeartButton::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto amount = _fill.value(_active ? 1. : 0.);
	const auto hover = _hover.value(isOver() ? 1. : 0.);
	auto red = st::attentionButtonFg->c;
	red.setAlphaF(.75);
	auto outline = amount > 0. ? red : st::windowSubTextFg->c;
	auto fill = red;
	fill.setAlphaF(.6 * amount);
	const auto size = st::musigramCatalogHeartSize * (1. + .08 * hover);
	DrawHeart(p, QRectF((width() - size) / 2., (height() - size) / 2., size, size), outline, fill);
}

class WavePanel final : public Ui::RpWidget {
public:
	WavePanel(QWidget *parent);
protected:
	void paintEvent(QPaintEvent *event) override;
	int resizeGetHeight(int width) override;

};

WavePanel::WavePanel(QWidget *parent) : RpWidget(parent) {
	setAccessibleName(tr::lng_musigram_wave(tr::now) + u" · "_q + tr::lng_musigram_wave_hint(tr::now));
}

int WavePanel::resizeGetHeight(int) {
	return st::musigramCatalogHeroHeight;
}

void WavePanel::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto pad = st::musigramCatalogPadding;
	const auto wide = width() >= st::musigramCatalogWaveMoodsAt;
	const auto center = QPointF(width() * .5, height() * .5);
	const auto orb = std::min(height() * .35, width() * (wide ? .22 : .36));
	p.setPen(Qt::NoPen);
	p.setBrush(st::windowBgOver->c);
	p.drawRoundedRect(rect().adjusted(0, 0, -1, -1), st::musigramCatalogRadius, st::musigramCatalogRadius);
	auto glow = QRadialGradient(center, orb * 1.5);
	auto accent = st::windowActiveTextFg->c;
	accent.setAlpha(100);
	glow.setColorAt(0, accent);
	accent.setAlpha(0);
	glow.setColorAt(1, accent);
	p.setBrush(glow);
	p.drawEllipse(center, orb * 1.5, orb * 1.5);
	for (auto layer = 0; layer != 7; ++layer) {
		auto path = QPainterPath();
		for (auto i = 0; i <= 180; ++i) {
			const auto angle = i * 2. * 3.141592653589793 / 180.;
			const auto r = orb * (1. - .075 * layer) * (1. + .12 * std::sin(angle * 3. + layer * .6));
			const auto point = center + QPointF(std::cos(angle) * r, std::sin(angle) * r * .82);
			if (!i) path.moveTo(point); else path.lineTo(point);
		}
		auto stroke = st::windowActiveTextFg->c;
		stroke.setAlpha(220 - 20 * layer);
		p.setPen(QPen(stroke, st::lineWidth * (layer ? 2 : 4)));
		p.setBrush(Qt::NoBrush);
		p.drawPath(path);
	}
	p.setFont(st::musigramCatalogHeroTitle->f);
	p.setPen(st::windowFg->c);
	p.drawText(QRect(center.x() - orb * 1.5, pad, orb * 3., st::musigramCatalogSearchHeight), Qt::AlignCenter, tr::lng_musigram_wave(tr::now));
	p.setFont(st::musigramCatalogCardFont->f);
	p.setPen(st::windowActiveTextFg->c);
	p.drawText(QRect(center.x() - orb, center.y() - pad, orb * 2, pad * 2), Qt::AlignCenter, tr::lng_musigram_wave_hint(tr::now));
	if (wide) {
		const auto moods = std::vector<QString>{ tr::lng_musigram_focus(tr::now), tr::lng_musigram_evening(tr::now), tr::lng_musigram_indie(tr::now), tr::lng_musigram_energy(tr::now) };
		for (auto i = 0; i != int(moods.size()); ++i) {
			const auto y = pad * 3 + i * st::musigramCatalogRow.height;
			p.setPen(st::windowActiveTextFg->c);
			p.drawText(QRect(pad, y, pad * 2, pad * 2), Qt::AlignCenter, u"✦"_q);
			p.setPen(st::windowSubTextFg->c);
			p.drawText(QRect(pad * 3, y, width() * .35 - pad * 3, pad * 2), Qt::AlignVCenter, moods[i]);
		}
	}
	p.setFont(st::normalFont->f);
	p.setPen(st::windowSubTextFg->c);
	p.drawText(QRect(pad, height() - pad * 3, width() - 2 * pad, pad * 2), Qt::AlignCenter, tr::lng_musigram_discovery_hint(tr::now));
}

class ArtistTile final : public Ui::AbstractButton {
public:
	ArtistTile(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonObject artist, bool album = false);
	QString accessibilityName() override;
protected:
	void paintEvent(QPaintEvent *event) override;
	int resizeGetHeight(int width) override;
	void onStateChanged(State was, StateChangeSource source) override;
private:
	void updateFavorite();
	const not_null<Media::Player::CatalogBackend*> _backend;
	QJsonObject _artist;
	HeartButton _favorite;
	Ui::Animations::Simple _hover;
	bool _favoritePending = false;
	bool _album = false;

};

ArtistTile::ArtistTile(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonObject artist, bool album)
: AbstractButton(parent), _backend(backend), _artist(std::move(artist)), _favorite(this), _album(album) {
	setPointerCursor(true);
	_favorite.setVisible(!_album);
	_favorite.setClickedCallback([=] {
		if (_favoritePending) return;
		_favoritePending = true;
		_backend->send({ { u"command"_q, u"favoriteArtist"_q }, { u"artist"_q, _artist }, { u"favorite"_q, !_backend->isFavoriteArtist(_artist.value(u"Id"_q).toString()) } });
	});
	updateFavorite();
	_backend->messages() | rpl::on_next([=](const QJsonObject &message) {
		if (message.value(u"Event"_q).toString() == u"artwork"_q) update();
		const auto command = message.value(u"Command"_q).toString();
		if (command == u"favoriteArtist"_q || command == u"library"_q) {
			_favoritePending = false;
			updateFavorite();
		}
	}, lifetime());
}

void ArtistTile::updateFavorite() {
	const auto active = _backend->isFavoriteArtist(_artist.value(u"Id"_q).toString());
	_favorite.setActive(active);
	const auto name = active ? tr::lng_musigram_unfavorite_artist(tr::now) : tr::lng_musigram_favorite_artist(tr::now);
	_favorite.setAccessibleName(name);
	_favorite.setToolTip(name);
}

QString ArtistTile::accessibilityName() {
	return _artist.value(u"Name"_q).toString() + u" · "_q + (_album ? tr::lng_musigram_album(tr::now) : tr::lng_musigram_artist(tr::now));
}

int ArtistTile::resizeGetHeight(int width) {
	_favorite.move(width - _favorite.width(), 0);
	return st::musigramCatalogArtistHeight;
}

void ArtistTile::onStateChanged(State, StateChangeSource) {
	_hover.start([=] { update(); }, _hover.value(isOver() ? 0. : 1.), isOver() ? 1. : 0., st::musigramCatalogAnimationDuration, anim::easeOutCirc);
}

void ArtistTile::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto gap = st::musigramCatalogGap;
	const auto cover = std::min(st::musigramCatalogArtistCover, std::max(0, width() - 2 * gap));
	const auto imageRect = QRect((width() - cover) / 2, gap, cover, cover);
	const auto hover = _hover.value(isOver() ? 1. : 0.);
	if (hover > 0.) {
		p.setPen(Qt::NoPen);
		auto background = st::windowBgOver->c;
		background.setAlphaF(hover);
		p.setBrush(background);
		p.drawRoundedRect(rect(), gap, gap);
	}
	auto clip = QPainterPath();
	if (_album) clip.addRoundedRect(imageRect, gap, gap); else clip.addEllipse(imageRect);
	p.save();
	p.setClipPath(clip);
	p.fillRect(imageRect, st::windowBgOver);
	const auto image = _backend->cover(_artist.value(u"Thumbnail"_q).toString());
	if (!image.isNull()) {
		DrawSquareCover(p, imageRect, image);
	} else {
		p.setPen(st::windowActiveTextFg->c);
		p.setFont(st::musigramCatalogHeading->f);
		p.drawText(imageRect, Qt::AlignCenter, _artist.value(u"Name"_q).toString().left(1));
	}
	p.restore();
	const auto name = _artist.value(u"Name"_q).toString();
	p.setPen(st::windowFg->c);
	p.setFont(st::musigramCatalogTrackFont->f);
	p.drawText(QRect(gap, gap * 2 + cover, width() - 2 * gap, gap * 2), Qt::AlignCenter, QFontMetrics(p.font()).elidedText(name, Qt::ElideRight, width() - 2 * gap));
	p.setFont(st::normalFont->f);
	p.setPen(st::windowSubTextFg->c);
	const auto audience = _artist.value(u"MonthlyListeners"_q).toObject().value(u"Display"_q).toString();
	auto subtitle = audience.isEmpty() ? tr::lng_musigram_artist(tr::now) : audience + u" / "_q + tr::lng_musigram_monthly_short(tr::now);
	if (_album) {
		subtitle = _artist.value(u"Artist"_q).toString();
		const auto year = _artist.value(u"Year"_q).toString();
		if (!subtitle.isEmpty() && !year.isEmpty()) subtitle += u" · "_q;
		subtitle += year;
	}
	p.drawText(QRect(gap, gap * 4 + cover, width() - 2 * gap, gap * 2), Qt::AlignCenter, QFontMetrics(p.font()).elidedText(subtitle, Qt::ElideRight, width() - 2 * gap));
}

class ArtistGrid final : public Ui::RpWidget {
public:
	ArtistGrid(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonArray artists, Fn<void(QJsonObject)> choose, bool albums = false, bool sortAlbums = true);
protected:
	int resizeGetHeight(int width) override;
private:
	std::vector<not_null<ArtistTile*>> _tiles;

};

ArtistGrid::ArtistGrid(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonArray artists, Fn<void(QJsonObject)> choose, bool albums, bool sortAlbums)
: RpWidget(parent) {
	auto ordered = std::vector<QJsonObject>();
	for (const auto &value : artists) ordered.push_back(value.toObject());
	if (albums && sortAlbums) {
		std::stable_sort(ordered.begin(), ordered.end(), [](const auto &a, const auto &b) {
			return a.value(u"Year"_q).toString().toInt() > b.value(u"Year"_q).toString().toInt();
		});
	}
	for (const auto &artist : ordered) {
		const auto tile = Ui::CreateChild<ArtistTile>(this, backend, artist, albums);
		tile->setClickedCallback([=] { choose(artist); });
		_tiles.push_back(tile);
	}
}

int ArtistGrid::resizeGetHeight(int width) {
	const auto gap = st::musigramCatalogGap;
	const auto columns = std::max(1, width / st::musigramCatalogArtistWidth);
	const auto tileWidth = std::max(0, (width - (columns - 1) * gap) / columns);
	for (auto i = 0; i != int(_tiles.size()); ++i) {
		_tiles[i]->resizeToWidth(tileWidth);
		_tiles[i]->move((i % columns) * (tileWidth + gap), (i / columns) * st::musigramCatalogArtistHeight);
	}
	return ((int(_tiles.size()) + columns - 1) / columns) * st::musigramCatalogArtistHeight;
}

class ArtistBanner final : public Ui::RpWidget {
public:
	ArtistBanner(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonObject artist);
protected:
	void paintEvent(QPaintEvent *event) override;
	int resizeGetHeight(int width) override;
private:
	const not_null<Media::Player::CatalogBackend*> _backend;
	QJsonObject _artist;

};

ArtistBanner::ArtistBanner(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonObject artist)
: RpWidget(parent), _backend(backend), _artist(std::move(artist)) {
	_backend->messages() | rpl::on_next([=](const QJsonObject &message) {
		if (message.value(u"Event"_q).toString() == u"artwork"_q) update();
	}, lifetime());
}

int ArtistBanner::resizeGetHeight(int) {
	return st::musigramCatalogArtistHeight;
}

void ArtistBanner::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto pad = st::musigramCatalogPadding;
	const auto cover = std::min(st::musigramCatalogArtistCover, width() / 3);
	const auto imageRect = QRect(pad, (height() - cover) / 2, cover, cover);
	auto gradient = QLinearGradient(QPointF(), QPointF(width(), height()));
	auto accent = st::windowActiveTextFg->c;
	accent.setAlpha(70);
	gradient.setColorAt(0, accent);
	gradient.setColorAt(1, st::windowBg->c);
	p.setBrush(gradient);
	p.setPen(Qt::NoPen);
	p.drawRoundedRect(rect(), st::musigramCatalogRadius, st::musigramCatalogRadius);
	auto clip = QPainterPath();
	clip.addEllipse(imageRect);
	p.save();
	p.setClipPath(clip);
	const auto image = _backend->cover(_artist.value(u"Thumbnail"_q).toString());
	if (!image.isNull()) DrawSquareCover(p, imageRect, image);
	p.restore();
	const auto left = cover + pad * 2;
	const auto textWidth = std::max(0, width() - left - pad);
	const auto album = _artist.value(u"Id"_q).toString().startsWith(u"MPRE"_q);
	p.setPen(st::windowSubTextFg->c);
	p.setFont(st::normalFont->f);
	p.drawText(left, pad * 2, album ? tr::lng_musigram_album(tr::now) : tr::lng_musigram_artist(tr::now));
	p.setPen(st::windowFg->c);
	p.setFont(st::musigramCatalogHeroTitle->f);
	p.drawText(left, pad * 4, QFontMetrics(p.font()).elidedText(_artist.value(u"Name"_q).toString(), Qt::ElideRight, textWidth));
	p.setPen(st::windowActiveTextFg->c);
	p.setFont(st::musigramCatalogHeading->f);
	const auto audience = _artist.value(u"MonthlyListeners"_q).toObject().value(u"Display"_q).toString();
	p.drawText(left, pad * 6, album ? _artist.value(u"Year"_q).toString() : audience.isEmpty() ? u"—"_q : audience);
	p.setPen(st::windowSubTextFg->c);
	p.setFont(st::normalFont->f);
	p.drawText(QRect(left, pad * 7, textWidth, pad * 3), Qt::TextWordWrap, album ? _artist.value(u"Artist"_q).toString() : audience.isEmpty() ? tr::lng_musigram_no_audience(tr::now) : tr::lng_musigram_monthly_audience(tr::now));
}

void DrawCardArtwork(QPainter &p, QRectF target, const QString &theme) {
	p.save();
	p.translate(target.topLeft());
	p.scale(target.width() / 100., target.height() / 100.);
	auto ink = st::windowActiveTextFg->c;
	auto fill = ink;
	fill.setAlpha(45);
	p.setPen(QPen(ink, 2.2, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
	p.setBrush(fill);
	if (theme == u"pop"_q) {
		p.translate(50, 50); p.rotate(24); p.translate(-50, -50);
		p.drawRoundedRect(QRectF(36, 8, 28, 49), 14, 14);
		for (auto y = 21; y < 48; y += 7) p.drawLine(QPointF(41, y), QPointF(59, y));
		auto stand = QPainterPath();
		stand.moveTo(28, 43); stand.cubicTo(28, 78, 72, 78, 72, 43);
		p.setBrush(Qt::NoBrush); p.drawPath(stand);
		p.drawLine(QPointF(50, 70), QPointF(50, 89));
		p.drawLine(QPointF(35, 90), QPointF(65, 90));
	} else if (theme == u"rock"_q || theme == u"indie"_q) {
		const auto electric = theme == u"rock"_q;
		p.translate(50, 50); p.rotate(electric ? 30 : -24); p.translate(-50, -50);
		auto body = QPainterPath();
		body.moveTo(44, 44);
		body.cubicTo(25, 29, electric ? 43 : 12, 53, 25, 63);
		body.cubicTo(10, 94, 83, 99, 76, 66);
		body.cubicTo(73, 54, electric ? 51 : 85, 33, 56, 44);
		body.closeSubpath(); p.drawPath(body);
		p.drawRoundedRect(QRectF(45, 13, 10, 49), 2, 2);
		p.drawRoundedRect(QRectF(42, 3, 16, 18), 3, 3);
		p.setBrush(st::windowBgOver->c);
		if (electric) p.drawRoundedRect(QRectF(39, 65, 22, 9), 2, 2);
		else p.drawEllipse(QPointF(50, 61), 8, 8);
		p.drawLine(QPointF(37, 82), QPointF(63, 82));
		for (auto x = 47; x <= 53; x += 3) p.drawLine(QPointF(x, 18), QPointF(x, 81));
	} else if (theme == u"hiphop"_q) {
		p.translate(50, 50); p.rotate(-12); p.translate(-50, -50);
		p.drawRoundedRect(QRectF(5, 23, 90, 62), 8, 8);
		p.drawEllipse(QPointF(38, 54), 25, 25);
		p.setBrush(Qt::NoBrush); p.drawEllipse(QPointF(38, 54), 17, 17);
		p.setBrush(ink); p.drawEllipse(QPointF(38, 54), 6, 6);
		auto arm = QPainterPath(); arm.moveTo(79, 35); arm.lineTo(73, 58); arm.lineTo(62, 67);
		p.setBrush(Qt::NoBrush); p.drawPath(arm);
		p.drawRoundedRect(QRectF(59, 64, 9, 5), 1, 1);
		p.drawEllipse(QPointF(79, 35), 4, 4);
		p.drawLine(QPointF(76, 75), QPointF(86, 75));
	} else if (theme == u"electronic"_q) {
		p.translate(50, 50); p.rotate(-14); p.translate(-50, -50);
		p.drawRoundedRect(QRectF(6, 24, 88, 54), 7, 7);
		for (auto x = 16; x < 85; x += 11) {
			p.drawRoundedRect(QRectF(x, 48, 9, 24), 1, 1);
			if (x < 75) { p.setBrush(ink); p.drawRect(QRectF(x + 7, 48, 5, 13)); p.setBrush(fill); }
		}
		for (auto x = 18; x < 52; x += 12) p.drawEllipse(QPointF(x, 36), 3, 3);
		p.drawRoundedRect(QRectF(59, 31, 24, 10), 2, 2);
	} else if (theme == u"focus"_q) {
		auto band = QPainterPath(); band.moveTo(17, 61); band.lineTo(17, 43);
		band.cubicTo(17, 1, 83, 1, 83, 43); band.lineTo(83, 61);
		p.setBrush(Qt::NoBrush); p.drawPath(band);
		p.setBrush(fill); p.drawRoundedRect(QRectF(12, 45, 18, 32), 7, 7);
		p.drawRoundedRect(QRectF(70, 45, 18, 32), 7, 7);
		p.drawLine(QPointF(37, 65), QPointF(37, 81));
		p.drawLine(QPointF(50, 52), QPointF(50, 90));
		p.drawLine(QPointF(63, 65), QPointF(63, 81));
	} else if (theme == u"evening"_q) {
		auto moon = QPainterPath(); moon.addEllipse(QRectF(15, 17, 65, 65));
		auto cut = QPainterPath(); cut.addEllipse(QRectF(40, 5, 62, 62));
		p.drawPath(moon.subtracted(cut));
		for (const auto point : { QPointF(78, 17), QPointF(85, 70), QPointF(59, 87) }) {
			p.drawLine(point + QPointF(-4, 0), point + QPointF(4, 0));
			p.drawLine(point + QPointF(0, -4), point + QPointF(0, 4));
		}
	} else if (theme == u"energy"_q) {
		p.drawRoundedRect(QRectF(19, 7, 62, 86), 9, 9);
		p.drawEllipse(QPointF(50, 30), 12, 12);
		p.drawEllipse(QPointF(50, 66), 20, 20);
		auto bolt = QPainterPath(); bolt.moveTo(58, 34); bolt.lineTo(39, 61);
		bolt.lineTo(52, 61); bolt.lineTo(44, 82); bolt.lineTo(68, 52); bolt.lineTo(54, 52); bolt.closeSubpath();
		p.setBrush(ink); p.drawPath(bolt);
	} else {
		p.drawRoundedRect(QRectF(15, 27, 70, 60), 7, 7);
		p.drawLine(QPointF(23, 18), QPointF(77, 18));
		p.drawLine(QPointF(31, 10), QPointF(69, 10));
		p.drawLine(QPointF(30, 45), QPointF(70, 45));
		p.drawLine(QPointF(30, 58), QPointF(65, 58));
		p.drawLine(QPointF(30, 71), QPointF(58, 71));
	}
	p.restore();
}

class DiscoveryTile final : public Ui::AbstractButton {
public:
	DiscoveryTile(QWidget *parent, QString title, QString subtitle, bool hero, int variant = 0, bool favorites = false, QString theme = {});
	QString accessibilityName() override;
protected:
	void paintEvent(QPaintEvent *event) override;
	int resizeGetHeight(int width) override;
	void onStateChanged(State was, StateChangeSource source) override;
private:
	QString _title;
	QString _subtitle;
	QString _theme;
	bool _hero;
	int _variant;
	bool _favorites;
	Ui::Animations::Simple _hover;

};

DiscoveryTile::DiscoveryTile(QWidget *parent, QString title, QString subtitle, bool hero, int variant, bool favorites, QString theme)
: AbstractButton(parent)
, _title(std::move(title))
, _subtitle(std::move(subtitle))
, _theme(std::move(theme))
, _hero(hero)
, _variant(variant)
, _favorites(favorites) {
	setPointerCursor(true);
}

QString DiscoveryTile::accessibilityName() {
	return _title + u". "_q + _subtitle;
}

int DiscoveryTile::resizeGetHeight(int) {
	return _hero ? st::musigramCatalogHeroHeight : st::musigramCatalogCardHeight;
}

void DiscoveryTile::onStateChanged(State, StateChangeSource) {
	_hover.start([=] { update(); }, _hover.value(isOver() ? 0. : 1.), isOver() ? 1. : 0., st::musigramCatalogAnimationDuration, anim::easeOutCirc);
}

void DiscoveryTile::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto pad = st::musigramCatalogPadding;
	const auto gap = st::musigramCatalogGap;
	const auto radius = st::musigramCatalogRadius;
	auto gradient = QLinearGradient(QPointF(0, 0), QPointF(width(), height()));
	gradient.setColorAt(0, st::windowBgOver->c);
	gradient.setColorAt(1, st::windowBg->c);
	p.setPen(Qt::NoPen);
	p.setBrush(gradient);
	p.drawRoundedRect(rect().adjusted(0, 0, -1, -1), radius, radius);
	auto glow = QRadialGradient(QPointF(width() * .78, height() * .5), width() * .55);
	auto accent = _favorites ? st::attentionButtonFg->c : st::windowActiveTextFg->c;
	accent.setAlpha(70 + int(50 * _hover.value(isOver() ? 1. : 0.)));
	glow.setColorAt(0, accent);
	accent.setAlpha(0);
	glow.setColorAt(1, accent);
	p.setBrush(glow);
	p.drawRoundedRect(rect().adjusted(0, 0, -1, -1), radius, radius);
	const auto center = QPointF(width() * .79, height() * .47);
	if (_favorites) {
		auto stroke = st::attentionButtonFg->c;
		stroke.setAlpha(120);
		auto fill = stroke;
		fill.setAlpha(65);
		const auto size = height() * .8;
		DrawHeart(p, QRectF(center.x() - size / 2., center.y() - size / 2., size, size), stroke, fill);
	} else {
		const auto size = std::min(height() * .88, width() * .36);
		DrawCardArtwork(p, QRectF(width() - pad - size, (height() - size) / 2., size, size), _theme);
	}
	p.setPen(st::windowFg->c);
	p.setFont((_hero ? st::musigramCatalogHeroTitle : st::musigramCatalogCardFont)->f);
	const auto textWidth = std::max(0, int(width() * .62) - pad);
	p.drawText(QRect(pad, 0, textWidth, height() - (_subtitle.isEmpty() ? 0 : pad)), Qt::AlignLeft | Qt::AlignVCenter, QFontMetrics(p.font()).elidedText(_title, Qt::ElideRight, textWidth));
	p.setFont(st::normalFont->f);
	p.setPen(st::windowSubTextFg->c);
	if (!_subtitle.isEmpty()) p.drawText(QRect(pad, height() / 2 + gap, textWidth, pad), Qt::AlignLeft | Qt::AlignVCenter, _subtitle);
}

class DiscoveryGrid final : public Ui::RpWidget {
public:
    DiscoveryGrid(QWidget *parent, std::vector<QString> titles, std::vector<QString> subtitles, Fn<void(int)> choose, Fn<void(int, QPoint)> menu = {}, std::vector<bool> favorites = {}, std::vector<QString> themes = {});
protected:
    int resizeGetHeight(int width) override;
private:
    std::vector<not_null<DiscoveryTile*>> _tiles;
};

DiscoveryGrid::DiscoveryGrid(QWidget *parent, std::vector<QString> titles, std::vector<QString> subtitles, Fn<void(int)> choose, Fn<void(int, QPoint)> menu, std::vector<bool> favorites, std::vector<QString> themes)
: RpWidget(parent) {
    for (auto i = 0; i != int(titles.size()); ++i) {
        const auto tile = Ui::CreateChild<DiscoveryTile>(this, titles[i], subtitles[i], false, i % st::musigramCatalogCardCount, i < int(favorites.size()) && favorites[i], i < int(themes.size()) ? themes[i] : QString());
        tile->setClickedCallback([=] { choose(i); });
        if (menu) {
            tile->setContextMenuPolicy(Qt::CustomContextMenu);
            QObject::connect(tile, &QWidget::customContextMenuRequested, tile, [=](QPoint point) { menu(i, tile->mapToGlobal(point)); });
        }
        _tiles.push_back(tile);
    }
}

int DiscoveryGrid::resizeGetHeight(int width) {
    const auto gap = st::musigramCatalogGap;
    const auto columns = width >= st::musigramCatalogGridWide ? st::musigramCatalogCardCount : 2;
    const auto tileWidth = std::max(0, (width - (columns - 1) * gap) / columns);
    for (auto i = 0; i != int(_tiles.size()); ++i) {
        _tiles[i]->resizeToWidth(tileWidth);
        _tiles[i]->move((i % columns) * (tileWidth + gap), (i / columns) * (st::musigramCatalogCardHeight + gap));
    }
    return ((int(_tiles.size()) + columns - 1) / columns) * (st::musigramCatalogCardHeight + gap);
}

class TrackRow final : public Ui::AbstractButton {
public:
	TrackRow(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonObject track, Fn<void(QJsonObject)> add);
	QString accessibilityName() override;
protected:
	void paintEvent(QPaintEvent *event) override;
	int resizeGetHeight(int width) override;
	void onStateChanged(State was, StateChangeSource source) override;
	void resizeEvent(QResizeEvent *event) override;
private:
	void updateLike();
	const not_null<Media::Player::CatalogBackend*> _backend;
	QJsonObject _track;
	HeartButton _like;
	Ui::RoundButton _add;
	bool _likePending = false;

};

TrackRow::TrackRow(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonObject track, Fn<void(QJsonObject)> add)
: AbstractButton(parent)
, _backend(backend)
, _track(std::move(track))
, _like(this)
, _add(this, rpl::single(u"＋"_q), st::defaultLightButton) {
	setPointerCursor(true);
	setIsListItem(true);
	_like.setClickedCallback([=] {
		if (_likePending) return;
		_likePending = true;
		_backend->send({ { u"command"_q, u"like"_q }, { u"track"_q, _track }, { u"liked"_q, !_backend->isLiked(_track.value(u"Url"_q).toString()) } });
	});
	_add.setToolTip(tr::lng_musigram_add(tr::now));
	_add.setClickedCallback([=] { add(_track); });
	updateLike();
	_backend->messages() | rpl::on_next([=](const QJsonObject &message) {
		const auto event = message.value(u"Event"_q).toString();
		const auto command = message.value(u"Command"_q).toString();
		if (command == u"like"_q || command == u"library"_q || command == u"playlistChange"_q) {
			_likePending = false;
			updateLike();
		}
		if ((event == u"artwork"_q && message.value(u"Url"_q) == _track.value(u"Thumbnail"_q)) || event == u"playback"_q) {
			update();
		}
	}, lifetime());
}

void TrackRow::updateLike() {
	const auto liked = _backend->isLiked(_track.value(u"Url"_q).toString());
	_like.setActive(liked);
	_like.setToolTip(liked ? tr::lng_musigram_unlike(tr::now) : tr::lng_musigram_like(tr::now));
	_like.setAccessibleName(liked ? tr::lng_musigram_unlike(tr::now) : tr::lng_musigram_like(tr::now));
	_add.setAccessibleName(tr::lng_musigram_add(tr::now));
}

void TrackRow::resizeEvent(QResizeEvent *event) {
	AbstractButton::resizeEvent(event);
	const auto size = st::musigramCatalogActionWidth;
	const auto pad = st::musigramCatalogPadding;
	_like.resize(size, _like.height());
	_add.resize(size, _add.height());
	_add.move(width() - pad - size, (height() - _add.height()) / 2);
	_like.move(_add.x() - size, (height() - _like.height()) / 2);
}

QString TrackRow::accessibilityName() {
	return _track.value(u"Title"_q).toString() + u" · "_q + _track.value(u"Artist"_q).toString();
}

int TrackRow::resizeGetHeight(int) {
	return st::musigramCatalogRow.height;
}

void TrackRow::onStateChanged(State, StateChangeSource) {
	update();
}

void TrackRow::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto pad = st::musigramCatalogPadding;
	const auto cover = st::musigramCatalogCoverSize;
	const auto gap = st::musigramCatalogGap;
	const auto active = _backend->playback().value(u"Track"_q).toObject().value(u"Url"_q) == _track.value(u"Url"_q);
	if (isOver() || active) {
		p.setPen(Qt::NoPen);
		p.setBrush(st::windowBgOver->c);
		p.drawRoundedRect(rect().adjusted(gap, 0, -gap, 0), gap, gap);
	}
	const auto image = _backend->cover(_track.value(u"Thumbnail"_q).toString());
	const auto imageRect = QRect(pad, (height() - cover) / 2, cover, cover);
	if (!image.isNull()) {
		p.drawImage(imageRect, image);
	} else {
		p.fillRect(imageRect, st::windowBgOver);
		p.setPen(st::windowActiveTextFg->c);
		p.setFont(st::musigramCatalogHeading->f);
		p.drawText(imageRect, Qt::AlignCenter, u"♪"_q);
	}
	const auto left = pad + cover + gap;
	const auto plays = _track.value(u"Plays"_q).toObject().value(u"Display"_q).toString();
	const auto showPlays = width() >= st::musigramCatalogSidebarAt;
	const auto controls = 2 * st::musigramCatalogActionWidth;
	const auto timeWidth = st::musigramCatalogSyncWidth;
	const auto right = pad + controls + timeWidth + (showPlays ? st::musigramCatalogArtistWidth : 0);
	const auto textWidth = std::max(0, width() - left - right);
	p.setFont(st::musigramCatalogTrackFont->f);
	p.setPen(active ? st::windowActiveTextFg->c : st::windowFg->c);
	p.drawText(left, height() / 2 - st::musigramCatalogTextSkip / 2,
		QFontMetrics(p.font()).elidedText(_track.value(u"Title"_q).toString(), Qt::ElideRight, textWidth));
	p.setFont(st::normalFont->f);
	p.setPen(st::windowSubTextFg->c);
	const auto subtitle = _track.value(u"Artist"_q).toString() + (showPlays || plays.isEmpty() ? QString() : u" · "_q + plays + u" "_q + tr::lng_musigram_plays(tr::now));
	p.drawText(left, height() / 2 + st::normalFont->height,
		QFontMetrics(p.font()).elidedText(subtitle, Qt::ElideRight, textWidth));
	if (showPlays) {
		p.drawText(QRect(width() - right, 0, st::musigramCatalogArtistWidth, height()), Qt::AlignLeft | Qt::AlignVCenter, plays.isEmpty() ? u"—"_q : plays + u" "_q + tr::lng_musigram_plays(tr::now));
	}
	const auto duration = int(_track.value(u"Duration"_q).toDouble());
	const auto time = QString::number(duration / 60) + u':' + QString::number(duration % 60).rightJustified(2, u'0');
	p.drawText(QRect(width() - pad - controls - timeWidth, 0, timeWidth - gap, height()), Qt::AlignRight | Qt::AlignVCenter, duration > 0 ? time : u"—"_q);
}

class TrackList final : public Ui::RpWidget {
public:
	TrackList(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonArray tracks, Fn<void(QJsonObject)> play, Fn<void(QJsonObject)> add, Fn<void(QJsonObject, QPoint)> menu);
protected:
	int resizeGetHeight(int width) override;
	void visibleTopBottomUpdated(int top, int bottom) override;
private:
	void updateRows();
	const not_null<Media::Player::CatalogBackend*> _backend;
	QJsonArray _tracks;
	Fn<void(QJsonObject)> _play;
	Fn<void(QJsonObject)> _add;
	Fn<void(QJsonObject, QPoint)> _menu;
	std::map<int, TrackRow*> _visible;
	int _top = 0;
	int _bottom = 0;
	int _rowWidth = 0;

};

TrackList::TrackList(QWidget *parent, not_null<Media::Player::CatalogBackend*> backend, QJsonArray tracks, Fn<void(QJsonObject)> play, Fn<void(QJsonObject)> add, Fn<void(QJsonObject, QPoint)> menu)
: RpWidget(parent), _backend(backend), _tracks(std::move(tracks)), _play(std::move(play)), _add(std::move(add)), _menu(std::move(menu)) {
}

int TrackList::resizeGetHeight(int width) {
	_rowWidth = width;
	updateRows();
	return _tracks.size() * st::musigramCatalogRow.height;
}

void TrackList::visibleTopBottomUpdated(int top, int bottom) {
	_top = top;
	_bottom = bottom;
	updateRows();
}

void TrackList::updateRows() {
	const auto height = st::musigramCatalogRow.height;
	const auto first = std::max(0, _top / height - 2);
	const auto last = std::min(int(_tracks.size()), std::max(first, (_bottom + height - 1) / height) + 2);
	for (auto i = _visible.begin(); i != _visible.end();) {
		if (i->first < first || i->first >= last) { delete i->second; i = _visible.erase(i); }
		else ++i;
	}
	for (auto i = first; i < last; ++i) {
		auto &row = _visible[i];
		if (!row) {
			const auto track = _tracks[i].toObject();
			row = Ui::CreateChild<TrackRow>(this, _backend, track, _add);
			row->setClickedCallback([=] { _play(track); });
			row->setContextMenuPolicy(Qt::CustomContextMenu);
			QObject::connect(row, &QWidget::customContextMenuRequested, row, [=, row = row](QPoint point) { _menu(track, row->mapToGlobal(point)); });
		}
		row->resizeToWidth(_rowWidth);
		row->move(0, i * height);
		row->show();
	}
}

std::shared_ptr<Window::SectionMemento> MakeArtistMemento(QJsonObject artist);

class PlayerGlyph final : public Ui::AbstractButton {
public:
    PlayerGlyph(QWidget *parent, QString kind, QString label, bool circle = false);
    void setGlyph(QString kind);
    void setActive(bool active);
    QString accessibilityName() override;
protected:
    void paintEvent(QPaintEvent *event) override;
    void onStateChanged(State was, StateChangeSource source) override;
private:
    QString _kind;
    bool _circle = false;
    bool _active = false;
    Ui::Animations::Simple _hover;

};

class PlayerTrack final : public Ui::AbstractButton {
public:
    explicit PlayerTrack(QWidget *parent);
    void setTrack(QJsonObject track);
    QString accessibilityName() override;
protected:
    void paintEvent(QPaintEvent *event) override;
private:
    QJsonObject _track;

};

class VolumePopover final : public Ui::RpWidget {
public:
	VolumePopover(QWidget *parent, Fn<void(int)> changed);
	void setExternalVolume(int volume, bool muted);
	void anchorHovered(QWidget *anchor, bool over);
	void reposition(QWidget *anchor);
	void forwardWheel(QWheelEvent *event);
	void hideFast();

protected:
	void paintEvent(QPaintEvent *event) override;
	void enterEventHook(QEnterEvent *event) override;
	void leaveEventHook(QEvent *event) override;
	void wheelEvent(QWheelEvent *event) override;
	void resizeEvent(QResizeEvent *event) override;

private:
	void animate(bool shown);
	void updateAppearance();
	void scheduleHide();
	void volumeChanged(float64 value, bool finished);
	void sendVolume();

	Ui::MediaSlider _slider;
	Fn<void(int)> _changed;
	Ui::Animations::Simple _appearance;
	base::Timer _hideTimer;
	base::Timer _sendTimer;
	bool _shown = false;
	bool _anchorOver = false;
	int _pendingVolume = 70;
	int _sentVolume = -1;
	crl::time _lastSent = 0;
	crl::time _ignoreExternalUntil = 0;

};

class CatalogBar final : public Ui::RpWidget {
public:
    CatalogBar(QWidget *parent, not_null<Window::SessionController*> controller, Fn<void(bool)> visibilityChanged);
protected:
    void resizeEvent(QResizeEvent *event) override;
    void paintEvent(QPaintEvent *event) override;
private:
    void receive(const QJsonObject &message);
    void showMenu();
    void showQueue(const QJsonObject &result);
    void showLyrics(const QJsonObject &result);
    void showEqualizer();
    void requestQueue();
    void requestLyrics();
    const not_null<Window::SessionController*> _controller;
    const not_null<Media::Player::CatalogBackend*> _backend;
    PlayerTrack _track;
    PlayerGlyph _dislike;
    PlayerGlyph _shuffle;
    Ui::IconButton _previous;
    PlayerGlyph _pause;
    Ui::IconButton _next;
    PlayerGlyph _repeat;
    HeartButton _like;
    PlayerGlyph _lyrics;
    PlayerGlyph _queue;
    PlayerGlyph _equalizer;
    PlayerGlyph _mute;
    PlayerGlyph _more;
    Ui::FilledSlider _seek;
    base::unique_qptr<VolumePopover> _volume;
    Ui::FlatLabel _time;
    QString _state;
    QString _thumbnail;
    QString _pendingLyrics;
    QJsonObject _currentTrack;
    QJsonObject _options;
    bool _queuePending = false;
    bool _muted = false;
    bool _disliked = false;
    base::unique_qptr<Ui::PopupMenu> _menu;
    Fn<void(bool)> _visibilityChanged;

};

class CatalogTabs final : public Ui::RpWidget {
public:
	CatalogTabs(QWidget *parent, bool albums, QString trackLabel, QString albumLabel, Fn<void(bool)> choose);
protected:
	int resizeGetHeight(int width) override;
private:
	Ui::RoundButton _tracks;
	Ui::RoundButton _albums;

};

CatalogTabs::CatalogTabs(QWidget *parent, bool albums, QString trackLabel, QString albumLabel, Fn<void(bool)> choose)
: RpWidget(parent)
, _tracks(this, rpl::single(trackLabel), albums ? st::defaultLightButton : st::defaultActiveButton)
, _albums(this, rpl::single(albumLabel), albums ? st::defaultActiveButton : st::defaultLightButton) {
	_tracks.setClickedCallback([=] { choose(false); });
	_albums.setClickedCallback([=] { choose(true); });
}

int CatalogTabs::resizeGetHeight(int width) {
	const auto gap = st::musigramCatalogGap;
	const auto half = std::max(0, (width - gap) / 2);
	_tracks.resize(half, _tracks.height());
	_albums.resize(half, _albums.height());
	_tracks.move(0, 0);
	_albums.move(half + gap, 0);
	return st::musigramCatalogToolbarHeight;
}

PlayerGlyph::PlayerGlyph(QWidget *parent, QString kind, QString label, bool circle)
: AbstractButton(parent), _kind(std::move(kind)), _circle(circle) {
    setAccessibleName(label);
    setToolTip(label);
    setPointerCursor(true);
    const auto size = circle ? st::musigramPlayerPlaySize : st::musigramPlayerControlSize;
    resize(size, size);
}

void PlayerGlyph::setGlyph(QString kind) { if (_kind == kind) return; _kind = std::move(kind); update(); }
void PlayerGlyph::setActive(bool active) { if (_active == active) return; _active = active; update(); }
QString PlayerGlyph::accessibilityName() { return accessibleName(); }
void PlayerGlyph::onStateChanged(State, StateChangeSource) {
    _hover.start([=] { update(); }, _hover.value(isOver() ? 0. : 1.), isOver() ? 1. : 0., st::musigramCatalogAnimationDuration, anim::easeOutCirc);
}

void PlayerGlyph::paintEvent(QPaintEvent *) {
    auto p = QPainter(this);
    p.setRenderHint(QPainter::Antialiasing);
    auto color = _active ? st::windowActiveTextFg->c : st::windowSubTextFg->c;
    if (_circle) {
        p.setPen(Qt::NoPen); p.setBrush(st::windowActiveTextFg->c); p.drawEllipse(rect());
        color = st::windowBg->c;
    } else if (_hover.value(isOver() ? 1. : 0.) > 0.) {
        auto background = st::windowBgOver->c;
        background.setAlphaF(_hover.value(isOver() ? 1. : 0.));
        p.setPen(Qt::NoPen); p.setBrush(background); p.drawRoundedRect(rect(), st::musigramCatalogGap, st::musigramCatalogGap);
    }
    const auto size = st::musigramPlayerGlyphSize;
    p.translate((width() - size) / 2., (height() - size) / 2.);
    p.scale(size / 24., size / 24.);
    p.setPen(QPen(color, 1.7, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin)); p.setBrush(Qt::NoBrush);
    if (_kind == u"play"_q) {
        auto path = QPainterPath(); path.moveTo(8, 4); path.lineTo(20, 12); path.lineTo(8, 20); path.closeSubpath(); p.fillPath(path, color);
    } else if (_kind == u"pause"_q) {
        p.fillRect(QRectF(6, 4, 4, 16), color); p.fillRect(QRectF(14, 4, 4, 16), color);
    } else if (_kind == u"cancel"_q) {
        p.drawLine(QPointF(6, 6), QPointF(18, 18)); p.drawLine(QPointF(18, 6), QPointF(6, 18));
    } else if (_kind == u"shuffle"_q) {
        auto path = QPainterPath(); path.moveTo(3, 5); path.cubicTo(12, 5, 12, 19, 21, 19); path.moveTo(3, 19); path.cubicTo(12, 19, 12, 5, 21, 5); p.drawPath(path);
        p.drawLine(QPointF(18, 2), QPointF(21, 5)); p.drawLine(QPointF(21, 5), QPointF(18, 8));
        p.drawLine(QPointF(18, 16), QPointF(21, 19)); p.drawLine(QPointF(21, 19), QPointF(18, 22));
    } else if (_kind == u"repeat"_q || _kind == u"one"_q) {
        auto path = QPainterPath(); path.moveTo(3, 10); path.lineTo(3, 7); path.quadTo(3, 4, 7, 4); path.lineTo(20, 4);
        path.moveTo(21, 14); path.lineTo(21, 17); path.quadTo(21, 20, 17, 20); path.lineTo(4, 20); p.drawPath(path);
        p.drawLine(QPointF(17, 1), QPointF(20, 4)); p.drawLine(QPointF(20, 4), QPointF(17, 7));
        p.drawLine(QPointF(7, 17), QPointF(4, 20)); p.drawLine(QPointF(4, 20), QPointF(7, 23));
        if (_kind == u"one"_q) { p.drawLine(QPointF(11, 10), QPointF(13, 8)); p.drawLine(QPointF(13, 8), QPointF(13, 16)); }
    } else if (_kind == u"dislike"_q) {
        DrawHeart(p, QRectF(1, 1, 22, 22), color, Qt::transparent); p.drawLine(QPointF(2, 2), QPointF(22, 22));
    } else if (_kind == u"queue"_q) {
        for (auto y = 5; y <= 19; y += 7) { p.drawEllipse(QPointF(3, y), 1, 1); p.drawLine(QPointF(8, y), QPointF(22, y)); }
    } else if (_kind == u"lyrics"_q) {
        p.drawLine(QPointF(3, 3), QPointF(17, 3)); p.drawLine(QPointF(10, 3), QPointF(10, 16));
        p.drawLine(QPointF(4, 20), QPointF(21, 20)); p.drawLine(QPointF(14, 12), QPointF(22, 12)); p.drawLine(QPointF(14, 16), QPointF(22, 16));
    } else if (_kind == u"equalizer"_q) {
        for (auto x = 5; x <= 19; x += 7) p.drawLine(QPointF(x, 2), QPointF(x, 22));
        p.setBrush(st::windowBg->c); p.drawEllipse(QPointF(5, 15), 3, 3); p.drawEllipse(QPointF(12, 7), 3, 3); p.drawEllipse(QPointF(19, 15), 3, 3);
    } else if (_kind == u"volume"_q || _kind == u"muted"_q) {
        auto path = QPainterPath(); path.moveTo(3, 8); path.lineTo(7, 8); path.lineTo(13, 3); path.lineTo(13, 21); path.lineTo(7, 16); path.lineTo(3, 16); path.closeSubpath(); p.drawPath(path);
        if (_kind == u"muted"_q) { p.drawLine(QPointF(17, 8), QPointF(23, 16)); p.drawLine(QPointF(23, 8), QPointF(17, 16)); }
        else { auto wave = QPainterPath(); wave.moveTo(17, 6); wave.quadTo(24, 12, 17, 18); p.drawPath(wave); }
    } else {
        p.setBrush(color); for (auto x = 4; x <= 20; x += 8) p.drawEllipse(QPointF(x, 12), 1.5, 1.5);
    }
}

PlayerTrack::PlayerTrack(QWidget *parent) : AbstractButton(parent) { setPointerCursor(true); }
void PlayerTrack::setTrack(QJsonObject track) { if (_track == track) return; _track = std::move(track); update(); }
QString PlayerTrack::accessibilityName() { return _track.value(u"Title"_q).toString() + u" · "_q + _track.value(u"Artist"_q).toString(); }
void PlayerTrack::paintEvent(QPaintEvent *) {
    auto p = QPainter(this);
    p.setFont(st::musigramCatalogTrackFont->f); p.setPen(st::windowFg->c);
    p.drawText(QRect(0, 0, width(), height() / 2), Qt::AlignLeft | Qt::AlignBottom, QFontMetrics(p.font()).elidedText(_track.value(u"Title"_q).toString(), Qt::ElideRight, width()));
    p.setFont(st::normalFont->f); p.setPen(st::windowSubTextFg->c);
    p.drawText(QRect(0, height() / 2 + st::lineWidth, width(), height() / 2), Qt::AlignLeft | Qt::AlignTop, QFontMetrics(p.font()).elidedText(_track.value(u"Artist"_q).toString(), Qt::ElideRight, width()));
}

VolumePopover::VolumePopover(QWidget *parent, Fn<void(int)> changed)
: RpWidget(parent)
, _slider(this, st::musigramVolumeSlider)
, _changed(std::move(changed))
, _hideTimer([=] {
	if (_slider.isChanging()) scheduleHide();
	else if (!_anchorOver && !rect().contains(mapFromGlobal(QCursor::pos()))) animate(false);
})
, _sendTimer([=] { sendVolume(); }) {
	setMouseTracking(true);
	_slider.setDirection(Ui::ContinuousSlider::Direction::Vertical);
	_slider.setAlwaysDisplayMarker(true);
	_slider.setMoveByWheel(true);
	_slider.setValue(.7);
	_slider.setAccessibleName(tr::lng_musigram_player_volume(tr::now));
	_slider.setChangeProgressCallback([=](float64 value) { volumeChanged(value, false); });
	_slider.setChangeFinishedCallback([=](float64 value) {
		volumeChanged(value, true);
		if (!_anchorOver && !rect().contains(mapFromGlobal(QCursor::pos()))) scheduleHide();
	});
	resize(st::musigramVolumePopupWidth, st::musigramVolumePopupHeight);
	hide();
}

void VolumePopover::setExternalVolume(int volume, bool muted) {
	if (_slider.isChanging() || crl::now() < _ignoreExternalUntil) return;
	_pendingVolume = muted ? 0 : std::clamp(volume, 0, 100);
	_sentVolume = _pendingVolume;
	_slider.setValue(_pendingVolume / 100.);
}

void VolumePopover::anchorHovered(QWidget *anchor, bool over) {
	_anchorOver = over;
	if (over) {
		_hideTimer.cancel();
		reposition(anchor);
		animate(true);
	} else {
		scheduleHide();
	}
}

void VolumePopover::reposition(QWidget *anchor) {
	const auto position = parentWidget()->mapFromGlobal(anchor->mapToGlobal(QPoint(anchor->width() / 2, 0)));
	const auto popupHeight = std::min(st::musigramVolumePopupHeight, std::max(0, position.y()));
	resize(st::musigramVolumePopupWidth, popupHeight);
	move(
		std::clamp(position.x() - width() / 2, 0, std::max(0, parentWidget()->width() - width())),
		std::max(0, position.y() - height()));
}

void VolumePopover::forwardWheel(QWheelEvent *event) {
	QApplication::sendEvent(&_slider, event);
	event->accept();
}

void VolumePopover::hideFast() {
	_hideTimer.cancel();
	_appearance.stop();
	_shown = false;
	_anchorOver = false;
	hide();
}

void VolumePopover::animate(bool shown) {
	if (_shown == shown) return;
	const auto from = _appearance.value(_shown ? 1. : 0.);
	_shown = shown;
	if (shown) { show(); raise(); }
	setAttribute(Qt::WA_TransparentForMouseEvents, !shown);
	_appearance.start(
		[=] { updateAppearance(); },
		from,
		shown ? 1. : 0.,
		st::musigramCatalogAnimationDuration,
		anim::easeOutCirc);
}

void VolumePopover::updateAppearance() {
	const auto opacity = _appearance.value(_shown ? 1. : 0.);
	_slider.setFadeOpacity(opacity);
	const auto padding = st::musigramVolumePopupPadding;
	const auto shift = int(base::SafeRound((1. - opacity) * st::musigramVolumePopupSlide));
	_slider.setGeometry(padding, padding + shift, width() - 2 * padding, std::max(0, height() - 2 * padding));
	if (!_shown && !_appearance.animating()) hide();
	update();
}

void VolumePopover::scheduleHide() { _hideTimer.callOnce(st::musigramVolumeHideDelay); }

void VolumePopover::volumeChanged(float64 value, bool finished) {
	_pendingVolume = std::clamp(int(base::SafeRound(value * 100)), 0, 100);
	_ignoreExternalUntil = crl::now() + st::musigramVolumeSettleDelay;
	if (finished || crl::now() - _lastSent >= st::musigramVolumeSendInterval) {
		_sendTimer.cancel();
		sendVolume();
	} else if (!_sendTimer.isActive()) {
		_sendTimer.callOnce(st::musigramVolumeSendInterval);
	}
}

void VolumePopover::sendVolume() {
	if (_pendingVolume == _sentVolume) return;
	_sentVolume = _pendingVolume;
	_lastSent = crl::now();
	_changed(_pendingVolume);
}

void VolumePopover::paintEvent(QPaintEvent *) {
	auto p = QPainter(this);
	p.setRenderHint(QPainter::Antialiasing);
	const auto opacity = _appearance.value(_shown ? 1. : 0.);
	p.setOpacity(opacity);
	const auto inset = st::musigramVolumePopupInset;
	const auto shift = (1. - opacity) * st::musigramVolumePopupSlide;
	const auto body = QRectF(inset, inset + shift, width() - 2 * inset, height() - 2 * inset);
	p.setPen(QPen(st::windowBgOver->c, st::lineWidth));
	p.setBrush(st::windowBg->c);
	p.drawRoundedRect(body, body.width() / 2., body.width() / 2.);
}

void VolumePopover::enterEventHook(QEnterEvent *event) {
	_hideTimer.cancel();
	animate(true);
	RpWidget::enterEventHook(event);
}

void VolumePopover::leaveEventHook(QEvent *event) {
	scheduleHide();
	RpWidget::leaveEventHook(event);
}

void VolumePopover::wheelEvent(QWheelEvent *event) { forwardWheel(event); }
void VolumePopover::resizeEvent(QResizeEvent *) { updateAppearance(); }

CatalogBar::CatalogBar(QWidget *parent, not_null<Window::SessionController*> controller, Fn<void(bool)> visibilityChanged)
: RpWidget(parent)
, _controller(controller)
, _backend(&controller->session().catalogMusic())
, _track(this)
, _dislike(this, u"dislike"_q, tr::lng_musigram_player_dislike(tr::now))
, _shuffle(this, u"shuffle"_q, tr::lng_musigram_player_shuffle(tr::now))
, _previous(this, st::mediaPlayerPreviousButton)
, _pause(this, u"play"_q, tr::lng_musigram_play(tr::now), true)
, _next(this, st::mediaPlayerNextButton)
, _repeat(this, u"repeat"_q, tr::lng_musigram_player_repeat(tr::now))
, _like(this)
, _lyrics(this, u"lyrics"_q, tr::lng_musigram_player_lyrics(tr::now))
, _queue(this, u"queue"_q, tr::lng_musigram_player_queue(tr::now))
, _equalizer(this, u"equalizer"_q, tr::lng_musigram_player_equalizer(tr::now))
, _mute(this, u"volume"_q, tr::lng_musigram_player_volume(tr::now))
, _more(this, u"more"_q, tr::lng_musigram_player_menu(tr::now))
, _seek(this, st::mediaPlayerPlayback)
, _volume(base::make_unique_q<VolumePopover>(parent, [=](int value) {
    _muted = false;
    _mute.setGlyph(value > 0 ? u"volume"_q : u"muted"_q);
    _backend->send({ { u"command"_q, u"volume"_q }, { u"value"_q, value } });
}))
, _time(this, st::defaultSubTextLabel)
, _visibilityChanged(std::move(visibilityChanged)) {
    setAttribute(Qt::WA_OpaquePaintEvent); hide();
    _track.setClickedCallback([=] {
        const auto id = _currentTrack.value(u"ArtistId"_q).toString();
        _controller->showSection(id.isEmpty() ? MakeCatalogMusic() : MakeArtistMemento({ { u"Id"_q, id }, { u"Name"_q, _currentTrack.value(u"Artist"_q) }, { u"Thumbnail"_q, _currentTrack.value(u"Thumbnail"_q) } }));
    });
    _previous.setClickedCallback([=] { _backend->send({ { u"command"_q, u"previous"_q } }); });
    _next.setClickedCallback([=] { _backend->send({ { u"command"_q, u"next"_q } }); });
    _pause.setClickedCallback([=] {
        if (_state == u"Loading"_q) _backend->send({ { u"command"_q, u"stop"_q } });
        else _backend->send({ { u"command"_q, u"pause"_q }, { u"paused"_q, _state == u"Playing"_q } });
    });
    _shuffle.setClickedCallback([=] { _backend->send({ { u"command"_q, u"controls"_q }, { u"shuffle"_q, !_options.value(u"Shuffle"_q).toBool() } }); });
    _repeat.setClickedCallback([=] {
        const auto repeat = _options.value(u"Repeat"_q).toString();
        _backend->send({ { u"command"_q, u"controls"_q }, { u"repeat"_q, repeat.isEmpty() || repeat == u"off"_q ? u"all"_q : repeat == u"all"_q ? u"one"_q : u"off"_q } });
    });
    _like.setClickedCallback([=] { _backend->send({ { u"command"_q, u"like"_q }, { u"track"_q, _currentTrack }, { u"liked"_q, !_backend->isLiked(_currentTrack.value(u"Url"_q).toString()) } }); });
    _dislike.setClickedCallback([=] { _backend->send({ { u"command"_q, u"dislike"_q }, { u"track"_q, _currentTrack }, { u"disliked"_q, !_disliked } }); });
    _mute.setClickedCallback([=] { _backend->send({ { u"command"_q, u"controls"_q }, { u"muted"_q, !_muted } }); });
    _lyrics.setClickedCallback([=] { requestLyrics(); });
    _queue.setClickedCallback([=] { requestQueue(); });
    _equalizer.setClickedCallback([=] { showEqualizer(); });
    _more.setClickedCallback([=] { showMenu(); });
    _seek.setChangeFinishedCallback([=](float64 position) { _backend->send({ { u"command"_q, u"seek"_q }, { u"position"_q, position } }); });
    _mute.setToolTip(QString());
    base::install_event_filter(this, &_mute, [=](not_null<QEvent*> event) {
        if (event->type() == QEvent::Enter) _volume->anchorHovered(&_mute, true);
        else if (event->type() == QEvent::Leave) _volume->anchorHovered(&_mute, false);
        else if (event->type() == QEvent::Wheel) {
            _volume->anchorHovered(&_mute, true);
            _volume->forwardWheel(static_cast<QWheelEvent*>(event.get()));
            return base::EventFilterResult::Cancel;
        }
        return base::EventFilterResult::Continue;
    });
    base::install_event_filter(this, this, [=](not_null<QEvent*> event) {
        if (event->type() == QEvent::Hide && _volume) _volume->hideFast();
        return base::EventFilterResult::Continue;
    });
    base::install_event_filter(this, window(), [=](not_null<QEvent*> event) {
        if (event->type() == QEvent::WindowDeactivate && _volume) _volume->hideFast();
        return base::EventFilterResult::Continue;
    });
    _backend->messages() | rpl::on_next([=](const QJsonObject &message) { receive(message); }, lifetime());
}

void CatalogBar::requestQueue() { _queuePending = true; _backend->send({ { u"command"_q, u"queueState"_q } }); }
void CatalogBar::requestLyrics() {
    if (_currentTrack.isEmpty()) return;
    _pendingLyrics = _currentTrack.value(u"Url"_q).toString();
    _backend->send({ { u"command"_q, u"lyrics"_q }, { u"track"_q, _currentTrack } });
}

void CatalogBar::receive(const QJsonObject &message) {
    const auto event = message.value(u"Event"_q).toString();
    const auto command = message.value(u"Command"_q).toString();
    const auto result = message.value(u"Result"_q).toObject();
    if (command == u"queueState"_q && _queuePending) { _queuePending = false; if (message.value(u"Ok"_q).toBool()) showQueue(result); }
    if (command == u"lyrics"_q && !_pendingLyrics.isEmpty()) {
        const auto error = message.value(u"Error"_q).toString();
        if (!error.isEmpty()) showLyrics({ { u"Text"_q, error } });
        else if (result.value(u"TrackUrl"_q).toString() == _pendingLyrics && _currentTrack.value(u"Url"_q).toString() == _pendingLyrics) showLyrics(result);
        _pendingLyrics.clear();
    }
    if (event != u"playback"_q && command != u"state"_q && event != u"artwork"_q && command != u"controls"_q && command != u"dislike"_q && command != u"like"_q && command != u"library"_q) return;
    const auto snapshot = _backend->playback();
    if (snapshot.isEmpty()) return;
    _currentTrack = snapshot.value(u"Track"_q).toObject();
    _track.setTrack(_currentTrack);
    _track.setToolTip(_track.accessibilityName() + u"\n"_q + snapshot.value(u"Error"_q).toString());
    _state = snapshot.value(u"State"_q).toString();
    _thumbnail = _currentTrack.value(u"Thumbnail"_q).toString();
    _pause.setGlyph(_state == u"Loading"_q ? u"cancel"_q : _state == u"Playing"_q ? u"pause"_q : u"play"_q);
    _pause.setAccessibleName(_state == u"Playing"_q ? tr::lng_musigram_player_pause(tr::now) : tr::lng_musigram_play(tr::now));
    const auto controls = _backend->controls();
    _options = controls.value(u"Options"_q).toObject();
    _muted = controls.value(u"Muted"_q).toBool(); _disliked = controls.value(u"Disliked"_q).toBool();
    _shuffle.setActive(_options.value(u"Shuffle"_q).toBool());
    const auto repeat = _options.value(u"Repeat"_q).toString();
    _repeat.setActive(!repeat.isEmpty() && repeat != u"off"_q); _repeat.setGlyph(repeat == u"one"_q ? u"one"_q : u"repeat"_q);
    _repeat.setToolTip(tr::lng_musigram_player_repeat(tr::now) + u": "_q + (repeat == u"one"_q ? tr::lng_musigram_player_repeat_one(tr::now) : repeat == u"all"_q ? tr::lng_musigram_player_repeat_all(tr::now) : tr::lng_musigram_player_repeat_off(tr::now)));
    _dislike.setActive(_disliked); _equalizer.setActive(_options.value(u"EqEnabled"_q).toBool());
    _mute.setGlyph(_muted || _options.value(u"Volume"_q).toInt(70) == 0 ? u"muted"_q : u"volume"_q);
    _like.setActive(_backend->isLiked(_currentTrack.value(u"Url"_q).toString()));
    _like.setAccessibleName(_backend->isLiked(_currentTrack.value(u"Url"_q).toString()) ? tr::lng_musigram_unlike(tr::now) : tr::lng_musigram_like(tr::now));
    if (command == u"state"_q || command == u"controls"_q || command == u"dislike"_q) {
        const auto length = controls.value(u"Length"_q).toDouble(); const auto time = controls.value(u"Time"_q).toDouble();
        if (!_seek.isChanging()) _seek.setValue(length > 0 ? time / length : 0);
        _volume->setExternalVolume(controls.value(u"Volume"_q).toInt(70), _muted);
        const auto format = [](double ms) { const auto seconds = std::max(0, int(ms / 1000)); return QString::number(seconds / 60) + u':' + QString::number(seconds % 60).rightJustified(2, u'0'); };
        _time.setText(format(time) + u" / "_q + format(length));
    }
    if (isHidden()) { show(); _visibilityChanged(true); }
    update();
}

void CatalogBar::showMenu() {
    _menu = base::make_unique_q<Ui::PopupMenu>(this);
    _menu->addAction(tr::lng_musigram_player_volume_level(tr::now), [=] {
        Ui::PostponeCall(this, [=] {
            _controller->show(Box([=](not_null<Ui::GenericBox*> box) {
                box->setTitle(tr::lng_musigram_player_volume_level());
                const auto slider = box->addRow(object_ptr<Ui::FilledSlider>(box, st::mediaPlayerPlayback));
                slider->resize(slider->width(), st::musigramCatalogSeekHeight);
                slider->setValue(_options.value(u"Volume"_q).toDouble(70) / 100.);
                slider->setChangeFinishedCallback([=](float64 value) { _backend->send({ { u"command"_q, u"volume"_q }, { u"value"_q, int(base::SafeRound(value * 100)) } }); });
                box->addButton(tr::lng_close(), [=] { box->closeBox(); });
            }));
        });
    });
    _menu->addAction(tr::lng_musigram_player_queue(tr::now), [=] { requestQueue(); });
    _menu->addAction(tr::lng_musigram_player_lyrics(tr::now), [=] { requestLyrics(); });
    _menu->addAction(tr::lng_musigram_player_equalizer(tr::now), [=] { Ui::PostponeCall(this, [=] { showEqualizer(); }); });
    _menu->addAction(tr::lng_musigram_add(tr::now), [=] {
        Ui::PostponeCall(this, [=] {
        _menu = base::make_unique_q<Ui::PopupMenu>(this);
        for (const auto &value : _backend->playlists()) {
            const auto playlist = value.toObject();
            _menu->addAction(playlist.value(u"Name"_q).toString(), [=] { _backend->send({ { u"command"_q, u"playlistChange"_q }, { u"kind"_q, u"add"_q }, { u"playlistId"_q, playlist.value(u"Id"_q) }, { u"track"_q, _currentTrack } }); });
        }
        _menu->popup(QCursor::pos());
        });
    });
    _menu->popup(QCursor::pos());
}

void CatalogBar::showQueue(const QJsonObject &result) {
    _menu = base::make_unique_q<Ui::PopupMenu>(this);
    const auto tracks = result.value(u"Tracks"_q).toArray();
    for (const auto &value : tracks) {
        const auto track = value.toObject();
        const auto prefix = track.value(u"Url"_q) == _currentTrack.value(u"Url"_q) ? tr::lng_musigram_player_current(tr::now) + u" · "_q : QString();
        _menu->addAction(prefix + track.value(u"Title"_q).toString() + u" · "_q + track.value(u"Artist"_q).toString(), [=] {
            _backend->send({ { u"command"_q, u"select"_q }, { u"track"_q, track }, { u"toggle"_q, true } });
        });
    }
    const auto offset = result.value(u"Offset"_q).toInt();
    const auto total = result.value(u"Total"_q).toInt();
    if (tracks.isEmpty()) _menu->addAction(tr::lng_musigram_player_queue_empty(tr::now), [] {});
    if (offset > 0) _menu->addAction(tr::lng_musigram_transfer_previous(tr::now), [=] { _queuePending = true; _backend->send({ { u"command"_q, u"queueState"_q }, { u"offset"_q, std::max(0, offset - st::musigramPlayerQueuePageSize) } }); });
    if (offset + tracks.size() < total) _menu->addAction(tr::lng_musigram_more(tr::now), [=] { _queuePending = true; _backend->send({ { u"command"_q, u"queueState"_q }, { u"offset"_q, offset + st::musigramPlayerQueuePageSize } }); });
    _menu->popup(QCursor::pos());
}

void CatalogBar::showLyrics(const QJsonObject &result) {
    const auto text = result.value(u"Text"_q).toString();
    _controller->show(Box([=](not_null<Ui::GenericBox*> box) {
        box->setTitle(tr::lng_musigram_player_lyrics()); box->setWidth(st::musigramTransferWidth); box->setMaxHeight(st::musigramTransferHeight);
        box->addRow(object_ptr<Ui::FlatLabel>(box, rpl::single(_track.accessibilityName()), st::defaultSubTextLabel));
        box->addRow(object_ptr<Ui::FlatLabel>(box, rpl::single(text), st::defaultFlatLabel));
        box->addRow(object_ptr<Ui::FlatLabel>(box, rpl::single(result.value(u"Source"_q).toString()), st::defaultSubTextLabel));
        box->addButton(tr::lng_close(), [=] { box->closeBox(); });
    }));
}

void CatalogBar::showEqualizer() {
    _controller->show(Box([=](not_null<Ui::GenericBox*> box) {
        box->setTitle(tr::lng_musigram_player_equalizer()); box->setWidth(st::musigramTransferWidth); box->setMaxHeight(st::musigramTransferHeight);
        struct State { QJsonArray bands; std::vector<Ui::FilledSlider*> sliders; Ui::FilledSlider *preamp = nullptr; };
        const auto state = box->lifetime().make_state<State>();
        state->bands = _options.value(u"Bands"_q).toArray();
        while (state->bands.size() < 10) state->bands.push_back(0.);
        const auto frequencies = std::array{ u"31 Hz"_q, u"63 Hz"_q, u"125 Hz"_q, u"250 Hz"_q, u"500 Hz"_q, u"1 kHz"_q, u"2 kHz"_q, u"4 kHz"_q, u"8 kHz"_q, u"16 kHz"_q };
        const auto off = box->addRow(object_ptr<Ui::RoundButton>(box, tr::lng_musigram_player_eq_off(), st::defaultLightButton));
        off->setClickedCallback([=] { _backend->send({ { u"command"_q, u"controls"_q }, { u"eqEnabled"_q, false } }); });
        const auto reset = box->addRow(object_ptr<Ui::RoundButton>(box, tr::lng_musigram_player_eq_flat(), st::defaultLightButton));
        reset->setClickedCallback([=] {
            for (auto i = 0; i != int(state->sliders.size()); ++i) { state->sliders[i]->setValue(.5); state->bands[i] = 0.; }
            if (state->preamp) state->preamp->setValue(.5);
            _backend->send({ { u"command"_q, u"controls"_q }, { u"eqEnabled"_q, true }, { u"bands"_q, state->bands }, { u"preamp"_q, 0. } });
        });
        box->addRow(object_ptr<Ui::FlatLabel>(box, tr::lng_musigram_player_preamp(), st::defaultSubTextLabel));
        const auto preamp = box->addRow(object_ptr<Ui::FilledSlider>(box, st::mediaPlayerPlayback));
        state->preamp = preamp;
        preamp->resize(preamp->width(), st::musigramCatalogSeekHeight);
        preamp->setValue((_options.value(u"Preamp"_q).toDouble() + 20.) / 40.);
        preamp->setChangeFinishedCallback([=](float64 value) { _backend->send({ { u"command"_q, u"controls"_q }, { u"eqEnabled"_q, true }, { u"preamp"_q, value * 40. - 20. } }); });
        for (auto i = 0; i != int(frequencies.size()); ++i) {
            box->addRow(object_ptr<Ui::FlatLabel>(box, rpl::single(frequencies[i] + u" · −20 / +20 dB"_q), st::defaultSubTextLabel));
            const auto slider = box->addRow(object_ptr<Ui::FilledSlider>(box, st::mediaPlayerPlayback));
            slider->resize(slider->width(), st::musigramCatalogSeekHeight);
            slider->setValue((state->bands[i].toDouble() + 20.) / 40.);
            slider->setChangeFinishedCallback([=](float64 value) {
                state->bands[i] = value * 40. - 20.;
                _backend->send({ { u"command"_q, u"controls"_q }, { u"eqEnabled"_q, true }, { u"bands"_q, state->bands } });
            });
            state->sliders.push_back(slider);
        }
        box->addButton(tr::lng_close(), [=] { box->closeBox(); });
    }));
}

void CatalogBar::resizeEvent(QResizeEvent *) {
    const auto gap = st::musigramPlayerGap;
    const auto pad = st::musigramCatalogGap;
    const auto cover = st::musigramCatalogMiniCoverSize;
    const auto top = (height() - st::musigramCatalogSeekHeight - _pause.height()) / 2;
    const auto right = width() >= st::musigramPlayerWideAt;
    _lyrics.setVisible(right); _equalizer.setVisible(right);
    _time.setVisible(width() >= st::musigramPlayerTimeAt);
    const auto centerWidgets = std::array<Ui::RpWidget*, 7>{ &_dislike, &_shuffle, &_previous, &_pause, &_next, &_repeat, &_like };
    auto centerWidth = int(centerWidgets.size() - 1) * gap;
    for (const auto widget : centerWidgets) centerWidth += widget->width();
    const auto rightWidth = _queue.width() + _mute.width() + _more.width() + 2 * gap + (right ? _lyrics.width() + _equalizer.width() + 2 * gap : 0);
    const auto left = cover + 2 * pad;
    const auto available = std::max(0, width() - left - rightWidth - centerWidth - 3 * gap - pad);
    const auto trackWidth = std::min(available, st::musigramPlayerTrackWidth);
    _track.setGeometry(left, top, trackWidth, _pause.height());
    auto x = std::max(left + trackWidth + gap, (width() - centerWidth) / 2);
    x = std::min(x, width() - rightWidth - centerWidth - 2 * gap - pad);
    for (const auto widget : centerWidgets) { widget->move(x, top + (_pause.height() - widget->height()) / 2); x += widget->width() + gap; }
    x = width() - rightWidth - pad;
    const auto trailing = std::array<Ui::RpWidget*, 5>{ &_lyrics, &_queue, &_equalizer, &_mute, &_more };
    for (const auto widget : trailing) { if (widget->isHidden()) continue; widget->move(x, top + (_pause.height() - widget->height()) / 2); x += widget->width() + gap; }
    _volume->reposition(&_mute);
    const auto timeWidth = st::musigramPlayerTimeWidth;
    _time.resizeToWidth(timeWidth); _time.move(width() - timeWidth - pad, height() - st::musigramCatalogSeekHeight);
    _seek.setGeometry(pad, height() - st::musigramCatalogSeekHeight, width() - 2 * pad - (_time.isHidden() ? 0 : timeWidth + gap), st::musigramCatalogSeekHeight);
}

void CatalogBar::paintEvent(QPaintEvent *event) {
    auto p = QPainter(this); p.fillRect(event->rect(), st::windowBg); p.fillRect(0, 0, width(), st::lineWidth, st::windowBgOver);
    const auto cover = st::musigramCatalogMiniCoverSize;
    const auto image = _backend->cover(_thumbnail);
    if (!image.isNull()) p.drawImage(QRect(st::musigramCatalogGap, (height() - st::musigramCatalogSeekHeight - cover) / 2, cover, cover), image);
}

class CatalogMemento final : public Window::SectionMemento {
public:
	explicit CatalogMemento(QJsonObject artist = {}) : _artist(std::move(artist)) {
	}
	object_ptr<Window::SectionWidget> createWidget(
		QWidget *parent,
		not_null<Window::SessionController*> controller,
		Window::Column column,
		const QRect &geometry) override;
private:
	QJsonObject _artist;

};

class CatalogWidget final : public Window::SectionWidget {
public:
	CatalogWidget(
		QWidget *parent,
		not_null<Window::SessionController*> controller);
	~CatalogWidget() override;
	void openInitialArtist(QJsonObject artist);
	bool showInternal(
		not_null<Window::SectionMemento*> memento,
		const Window::SectionShow &params) override;
	bool sameTypeAs(not_null<Window::SectionMemento*> memento) override;
	std::shared_ptr<Window::SectionMemento> createMemento() override;
	QRect floatPlayerAvailableRect() override;
	bool floatPlayerHandleWheelEvent(QEvent *event) override;

protected:
	void resizeEvent(QResizeEvent *event) override;
	void paintEvent(QPaintEvent *event) override;
	void doSetInnerFocus() override;
	void showFinishedHook() override;

private:
	void layout();
	void search();
	void updateSearchSource();
	void switchSearchSource();
	void receive(const QJsonObject &message);
	void renderTracks();
	bool isSearchLanding() const;
	void renderDiscovery();
	void refreshPlaylistLabel();
	void showHome();
	QString tasteKey() const;
	void chooseTaste();
	void requestRecommendations(bool refresh = false);
	void renderRecommendations();
	void openArtist(QJsonObject artist);
	void openAlbum(QJsonObject album);
	void openGenre(QString key, QString title);
	void loadMore();
	void choosePlaylist();
	void playlistNameBox(bool rename, QJsonObject track = {});
	void addContentMenu();
	void importBox();
	void renderImport();
	void receiveImport(const QJsonObject &message);
	void addLocalFiles();
	void deletePlaylist();
	void trackMenu(QJsonObject track, QPoint position);
	void addToPlaylist(QJsonObject track);
	void play(QJsonObject track);
	void playlistChange(QJsonObject request);
	void updatePlayback(const QJsonObject &snapshot);

	const not_null<Media::Player::CatalogBackend*> _backend;
	Ui::IconButton _back;
	Ui::FlatLabel _title;
	Ui::FlatLabel _heading;
	Ui::InputField _query;
	Ui::InputField _playlistQuery;
	Ui::RoundButton _search;
	Ui::FlatLabel _alternative;
	Ui::RoundButton _source;
	Ui::RoundButton _results;
	Ui::RoundButton _playlist;
	Ui::RoundButton _create;
	Ui::RoundButton _import;
	Ui::RoundButton _sync;
	Ui::FlatLabel _status;
	Ui::ScrollArea _scroll;
	not_null<Ui::VerticalLayout*> _rows;
	base::unique_qptr<Ui::PopupMenu> _menu;
	QPointer<Ui::GenericBox> _transferBox;
	Ui::InputField *_transferInput = nullptr;
	Ui::InputField *_transferName = nullptr;
	Ui::FlatLabel *_transferSummary = nullptr;
	Ui::VerticalLayout *_transferRows = nullptr;
	QJsonObject _transferPreview;
	int _transferPage = 0;
	bool _transferBusy = false;
	QJsonArray _shownTracks;
	QJsonArray _artists;
	QPointer<Ui::GenericBox> _tasteBox;
	bool _tasteReady = false;
	bool _tasteCompleted = false;
	bool _tasteOffered = false;
	QJsonObject _recommendations;
	QString _recommendationRequestKey;
	QString _recommendationPendingSeed;
	QString _recommendationError;
	bool _recommendationLoading = false;
	QJsonObject _artistProfile;
	QJsonObject _albumProfile;
	QJsonObject _genreProfile;
	QString _genreKey;
	QString _genreTitle;
	bool _genreFromHome = false;
	bool _genreLoading = false;
	QString _artistId;
	QString _albumId;
	QString _playlistId;
	QString _pendingPlaylistId;
	bool _playlistMode = false;
	bool _homeMode = true;
	bool _soundCloudMode = false;
	bool _artistFromHome = false;
	bool _artistAlbums = false;
	bool _loadingPage = false;
	base::Timer _suggestTimer;

};

object_ptr<Window::SectionWidget> CatalogMemento::createWidget(
		QWidget *parent,
		not_null<Window::SessionController*> controller,
		Window::Column,
		const QRect &geometry) {
	auto widget = object_ptr<CatalogWidget>(parent, controller);
	widget->setGeometry(geometry);
	if (!_artist.isEmpty()) widget->openInitialArtist(_artist);
	return widget;
}

CatalogWidget::CatalogWidget(
	QWidget *parent,
	not_null<Window::SessionController*> controller)
: SectionWidget(parent, controller)
, _backend(&session().catalogMusic())
, _back(this, st::infoTopBarBack)
, _title(this, rpl::single(u"MeloDesk"_q), st::infoTopBarTitle)
, _heading(this, tr::lng_musigram_my_music(), st::musigramCatalogHeadingLabel)
, _query(this, st::defaultInputField, tr::lng_musigram_search())
, _playlistQuery(this, st::defaultInputField, tr::lng_musigram_search())
, _search(this, tr::lng_musigram_find(), st::defaultActiveButton)
, _alternative(this, st::defaultSubTextLabel)
, _source(this, tr::lng_musigram_wave(), st::defaultLightButton)
, _results(this, tr::lng_musigram_results(), st::defaultLightButton)
, _playlist(this, tr::lng_musigram_collection(), st::defaultLightButton)
, _create(this, tr::lng_musigram_create(), st::defaultActiveButton)
, _import(this, tr::lng_musigram_add_content(), st::defaultLightButton)
, _sync(this, tr::lng_musigram_sync(), st::defaultLightButton)
, _status(this, st::defaultSubTextLabel)
, _scroll(this)
, _rows(_scroll.setOwnedWidget(object_ptr<Ui::VerticalLayout>(&_scroll)))
, _suggestTimer([=] {
	if (_playlistMode) {
		renderTracks();
	} else {
		search();
	}
}) {
	_back.setClickedCallback([=] {
		_suggestTimer.cancel();
		if (!_genreKey.isEmpty()) {
			_genreKey.clear();
			_homeMode = _genreFromHome;
			_status.setText(tr::lng_musigram_source_hint(tr::now));
			renderTracks();
			_scroll.scrollToY(0);
		} else if (!_albumId.isEmpty()) {
			_albumId.clear();
			_albumProfile = {};
			renderTracks();
		} else if (!_artistId.isEmpty()) {
			_artistId.clear();
			_artistProfile = {};
			_homeMode = _artistFromHome;
			renderTracks();
		} else if (_soundCloudMode && !_homeMode && !_playlistMode) {
			switchSearchSource();
		} else {
			this->controller()->showBackFromStack();
		}
	});
	_search.setClickedCallback([=] { search(); });
	updateSearchSource();
	_source.setClickedCallback([=] { showHome(); });
	_results.setClickedCallback([=] {
		_suggestTimer.cancel();
		_genreKey.clear();
		_playlistMode = false;
		_homeMode = false;
		_artistId.clear();
		_albumId.clear();
		_status.setText(_soundCloudMode
			? tr::lng_musigram_soundcloud_results(tr::now)
			: tr::lng_musigram_source_hint(tr::now));
		renderTracks();
		_query.setFocus();
	});
	_playlist.setClickedCallback([=] { choosePlaylist(); });
	_create.setClickedCallback([=] { playlistNameBox(false); });
	_import.setClickedCallback([=] { addContentMenu(); });
	_sync.setClickedCallback([=] {
		_backend->send({ { u"command"_q, u"library"_q } });
	});
	_playlistQuery.changes() | rpl::on_next([=] {
		if (!_playlistMode) return;
		_suggestTimer.cancel();
		_suggestTimer.callOnce(st::musigramCatalogSearchDelay);
	}, lifetime());
	_playlistQuery.submits() | rpl::on_next([=] {
		_suggestTimer.cancel();
		if (_playlistMode) renderTracks();
	}, lifetime());
	_query.submits() | rpl::on_next([=] { search(); }, lifetime());
	_query.changes() | rpl::on_next([=] {
		_suggestTimer.cancel();
		if (_query.isHidden()) return;
		const auto query = _query.getLastText().trimmed();
		if (query.isEmpty()) {
			_artists = {};
			_status.setText(_soundCloudMode
				? tr::lng_musigram_soundcloud_results(tr::now)
				: tr::lng_musigram_source_hint(tr::now));
			renderTracks();
		} else {
			if (query.size() == 1) { _artists = {}; renderTracks(); }
			if (query.size() >= 2) _suggestTimer.callOnce(st::musigramCatalogSearchDelay);
		}
	}, lifetime());
	_scroll.scrolls() | rpl::on_next([=] {
		const auto top = _scroll.scrollTop();
		_rows->setVisibleTopBottom(top, top + _scroll.height());
	}, lifetime());
	_backend->messages() | rpl::on_next([=](const QJsonObject &message) {
		receive(message);
	}, lifetime());
	_backend->registerSection(true);
	_status.setText(tr::lng_musigram_source_hint(tr::now));
	renderTracks();
	updatePlayback(_backend->playback());
	_backend->send({ { u"command"_q, u"library"_q } });
}

CatalogWidget::~CatalogWidget() {
	if (_tasteBox) _tasteBox->closeBox();
	_backend->registerSection(false);
}

void CatalogWidget::openInitialArtist(QJsonObject artist) {
	openArtist(std::move(artist));
}

void CatalogWidget::layout() {
	const auto pad = st::musigramCatalogPadding;
	const auto gap = st::musigramCatalogGap;
	const auto sidebar = width() >= st::musigramCatalogSidebarAt ? st::musigramCatalogSidebarWidth : 0;
	const auto content = std::max(0, width() - sidebar - 2 * pad);
	_back.move(0, 0);
	_title.resizeToWidth(std::max(0, width() - _back.width() - pad));
	_title.move(_back.width(), (st::infoTopBarHeight - _title.height()) / 2);
	auto top = st::infoTopBarHeight + gap;
	const auto searchVisible = !_homeMode
		&& !_playlistMode
		&& _genreKey.isEmpty()
		&& _artistId.isEmpty()
		&& _albumId.isEmpty();
	const auto playlistSearchVisible = _playlistMode && !_playlistId.isEmpty();
	_playlistQuery.setVisible(playlistSearchVisible);
	const auto focus = QApplication::focusWidget();
	if (!searchVisible && focus && (focus == &_query || _query.isAncestorOf(focus))) {
		_source.setFocus();
	}
	_query.setVisible(searchVisible);
	_search.setVisible(searchVisible);
	_alternative.setVisible(searchVisible);
	_query.resizeToWidth(std::max(0, content - st::musigramCatalogButtonWidth - gap));
	_query.move(sidebar + pad, top);
	_search.resize(st::musigramCatalogButtonWidth, _search.height());
	const auto textCenter = _query.fullTextMargins().top() + st::normalFont->height / 2;
	_search.move(width() - pad - _search.width(), top + textCenter - _search.height() / 2);
	_alternative.resizeToWidth(content);
	_alternative.move(sidebar + pad, top + std::max(_query.height(), textCenter + _search.height() / 2) + gap / 2);
	const auto searchBottom = _alternative.y() + _alternative.height() + gap;
	const auto tabs = std::array<Ui::RoundButton*, 3>{ &_source, &_results, &_playlist };
	if (sidebar) {
		for (auto i = 0; i != int(tabs.size()); ++i) {
			tabs[i]->resize(sidebar - 2 * gap, tabs[i]->height());
			tabs[i]->move(gap, top + i * (st::musigramCatalogToolbarHeight + gap));
		}
		if (searchVisible) top = searchBottom;
	} else {
		if (searchVisible) top = searchBottom;
		const auto third = std::max(0, (content - 2 * gap) / 3);
		for (auto i = 0; i != int(tabs.size()); ++i) {
			tabs[i]->resize(third, tabs[i]->height());
			tabs[i]->move(pad + i * (third + gap), top);
		}
		top += st::musigramCatalogToolbarHeight + gap;
	}
	const auto buttons = _playlistMode ? 2 * st::musigramCatalogButtonWidth + st::musigramCatalogSyncWidth + 2 * gap : 0;
	_heading.resizeToWidth(std::max(0, content - buttons));
	_heading.move(sidebar + pad, top);
	_create.setVisible(_playlistMode);
	_import.setVisible(_playlistMode);
	_sync.setVisible(_playlistMode);
	_create.resize(st::musigramCatalogButtonWidth, _create.height());
	_import.resize(st::musigramCatalogButtonWidth, _import.height());
	_sync.resize(st::musigramCatalogSyncWidth, _sync.height());
	_create.move(width() - pad - buttons, top);
	_import.move(_create.x() + _create.width() + gap, top);
	_sync.move(width() - pad - _sync.width(), top);
	top += st::musigramCatalogToolbarHeight;
	if (playlistSearchVisible) {
		_playlistQuery.resizeToWidth(content);
		_playlistQuery.move(sidebar + pad, top);
		top += _playlistQuery.height() + gap;
	}
	_status.resizeToWidth(content);
	_status.move(sidebar + pad, top);
	top += st::musigramCatalogStatusHeight;

	_scroll.setGeometry(sidebar, top, width() - sidebar, std::max(0, height() - top));
	_rows->resizeToWidth(_scroll.width());
	const auto visibleTop = _scroll.scrollTop();
	_rows->setVisibleTopBottom(visibleTop, visibleTop + _scroll.height());
	update();
}

void CatalogWidget::resizeEvent(QResizeEvent *event) {
	SectionWidget::resizeEvent(event);
	layout();
}

void CatalogWidget::showFinishedHook() {
	layout();
}

void CatalogWidget::paintEvent(QPaintEvent *event) {
	SectionWidget::paintEvent(event);
	if (!animatingShow()) {
		auto p = QPainter(this);
		p.fillRect(event->rect(), st::windowBg);
		p.fillRect(0, st::infoTopBarHeight - st::lineWidth, width(), st::lineWidth, st::windowBgOver);
		if (width() >= st::musigramCatalogSidebarAt) {
			p.fillRect(st::musigramCatalogSidebarWidth - st::lineWidth, 0,
				st::lineWidth, height(), st::windowBgOver);
		} else {
			p.fillRect(0, _source.y() - st::musigramCatalogGap / 2, width(), st::lineWidth, st::windowBgOver);
			p.fillRect(0, _source.y() + st::musigramCatalogToolbarHeight + st::musigramCatalogGap / 2,
				width(), st::lineWidth, st::windowBgOver);
		}
	}
}

void CatalogWidget::doSetInnerFocus() {
	if (!_query.isHidden()) _query.setFocus();
	else _source.setFocus();
}

bool CatalogWidget::showInternal(
		not_null<Window::SectionMemento*> memento,
		const Window::SectionShow &) {
	return sameTypeAs(memento);
}

bool CatalogWidget::sameTypeAs(not_null<Window::SectionMemento*> memento) {
	return dynamic_cast<CatalogMemento*>(memento.get()) != nullptr;
}

std::shared_ptr<Window::SectionMemento> CatalogWidget::createMemento() {
	return std::make_shared<CatalogMemento>();
}

QRect CatalogWidget::floatPlayerAvailableRect() {
	return QRect(mapToGlobal(QPoint()), size());
}

bool CatalogWidget::floatPlayerHandleWheelEvent(QEvent *) {
	return false;
}

void CatalogWidget::updateSearchSource() {
	_query.setPlaceholder(tr::lng_musigram_search());
	_alternative.setMarkedText(Ui::Text::Link(_soundCloudMode
		? tr::lng_musigram_youtube_return(tr::now)
		: tr::lng_musigram_soundcloud_alternative(tr::now), 1));
	_alternative.setLink(1, std::make_shared<LambdaClickHandler>([=] {
		switchSearchSource();
	}));
}

void CatalogWidget::switchSearchSource() {
	_suggestTimer.cancel();
	_soundCloudMode = !_soundCloudMode;
	_artists = {};
	updateSearchSource();
	if (!_query.getLastText().trimmed().isEmpty()) {
		search();
	} else {
		_status.setText(_soundCloudMode
			? tr::lng_musigram_soundcloud_results(tr::now)
			: tr::lng_musigram_source_hint(tr::now));
		renderTracks();
	}
	_scroll.scrollToY(0);
	_query.setFocus();
}

void CatalogWidget::search() {
	_suggestTimer.cancel();
	if (_playlistMode) {
		renderTracks();
		return;
	}
	_genreKey.clear();
	const auto query = _query.getLastText().trimmed();
	if (query.isEmpty()) {
		_query.setFocus();
		return;
	}
	_playlistMode = false;
	_homeMode = false;
	_artistId.clear();
	_albumId.clear();
	_artistProfile = {};
	_artists = {};
	_status.setText(tr::lng_musigram_searching(tr::now));
	_backend->send({
		{ u"command"_q, u"search"_q },
		{ u"query"_q, query },
		{ u"source"_q, _soundCloudMode ? u"SoundCloud"_q : u"YouTube Music"_q },
	});
	renderTracks();
}

void CatalogWidget::receive(const QJsonObject &message) {
	const auto error = message.value(u"Error"_q).toString();
	const auto command = message.value(u"Command"_q).toString();
	if (command == u"recommendations"_q) {
		if (message.value(u"Cancelled"_q).toBool()) return;
		const auto result = message.value(u"Result"_q).toObject();
		if (error.isEmpty() && result.value(u"RequestKey"_q).toString() != _recommendationRequestKey) return;
		_recommendationLoading = false;
		_recommendationError = error;
		if (error.isEmpty()) {
			const auto feed = result.value(u"Feed"_q).toObject();
			if (feed.value(u"SeedKey"_q).toString() == tasteKey()) _recommendations = feed;
		}
		if (_homeMode || isSearchLanding()) renderTracks();
		return;
	}
	if ((command == u"library"_q || command == u"favoriteArtist"_q || command == u"completeTaste"_q) && message.value(u"Ok"_q).toBool()) {
		const auto result = message.value(u"Result"_q).toObject();
		_tasteReady = true;
		_tasteCompleted = result.value(u"OnboardingCompleted"_q).toBool();
		if (!_tasteOffered && _homeMode && (!_tasteCompleted || _backend->favoriteArtists().size() < 5)) {
			_tasteOffered = true;
			QTimer::singleShot(0, this, [=] { if (_homeMode) chooseTaste(); });
		}
		if (command == u"completeTaste"_q) { if (_homeMode || isSearchLanding()) renderTracks(); return; }
	}
	if (command.startsWith(u"import"_q) || message.value(u"Event"_q).toString() == u"importProgress"_q) {
		receiveImport(message);
		return;
	}
	if (command == u"genre"_q || command == u"genrePage"_q) {
		if (_genreKey.isEmpty()) return;
		_genreLoading = false;
		_loadingPage = false;
		if (!error.isEmpty()) {
			_genreProfile.insert(u"Error"_q, error);
			_status.setText(error);
			renderTracks();
			return;
		}
		const auto result = message.value(u"Result"_q).toObject();
		if (command == u"genre"_q) {
			if (result.value(u"Key"_q).toString() != _genreKey) return;
			_genreProfile = result;
		} else {
			const auto page = result.value(u"Page"_q).toObject();
			if (page.value(u"OwnerId"_q).toString() != _genreKey || result.value(u"Previous"_q) != _genreProfile.value(u"Continuation"_q)) return;
			auto tracks = _genreProfile.value(u"Tracks"_q).toArray();
			auto urls = QSet<QString>();
			for (const auto &track : tracks) urls.insert(track.toObject().value(u"Url"_q).toString());
			for (const auto &track : page.value(u"Tracks"_q).toArray()) {
				const auto url = track.toObject().value(u"Url"_q).toString();
				if (!urls.contains(url)) { tracks.push_back(track); urls.insert(url); }
			}
			_genreProfile.insert(u"Tracks"_q, tracks);
			_genreProfile.insert(u"Continuation"_q, page.value(u"Continuation"_q));
		}
		_status.setText(tr::lng_musigram_source_hint(tr::now));
		renderTracks();
		return;
	}
	if (!error.isEmpty()) {
		_loadingPage = false;
		_status.setText(error);
		return;
	}
	if (command == u"localAdd"_q) {
		_playlistId = message.value(u"Result"_q).toObject().value(u"PlaylistId"_q).toString();
		_playlistMode = true; _homeMode = false;
		_genreKey.clear(); _artistId.clear(); _albumId.clear();
		refreshPlaylistLabel(); renderTracks();
		_status.setText(tr::lng_musigram_local_added(tr::now));
		return;
	}
	const auto event = message.value(u"Event"_q).toString();
	const auto result = message.value(u"Result"_q).toObject();
	if (event == u"playback"_q) {
		updatePlayback(message.value(u"Snapshot"_q).toObject());
	} else if (command == u"state"_q) {
        updatePlayback(result.value(u"Playback"_q).toObject());
	} else if (command == u"search"_q || event == u"searchBatch"_q) {
		if (command == u"search"_q && result.value(u"Query"_q).toString() != _query.getLastText().trimmed()) return;
		if (command == u"search"_q && result.value(u"Source"_q).toString() != (_soundCloudMode ? u"SoundCloud"_q : u"YouTube Music"_q)) return;
		if (command == u"search"_q) _artists = result.value(u"Artists"_q).toArray();
		if (!_playlistMode && !_homeMode && _artistId.isEmpty()) {
			renderTracks();
		}
		if (command == u"search"_q) {
			auto errors = QStringList();
			for (const auto &value : result.value(u"Messages"_q).toArray()) {
				errors.push_back(value.toString());
			}
			_status.setText(!errors.isEmpty()
				? errors.join(u"\n"_q)
				: _backend->tracks().isEmpty()
				? tr::lng_musigram_empty(tr::now)
				: tr::lng_musigram_results(tr::now));
		}
	} else if (command == u"artist"_q) {
		if (result.value(u"Id"_q).toString() == _artistId && !_artistId.isEmpty()) {
			_artistProfile = result;
			_status.setText(tr::lng_musigram_source_hint(tr::now));
			renderTracks();
		}
	} else if (command == u"album"_q) {
		if (result.value(u"Id"_q).toString() == _albumId && !_albumId.isEmpty()) {
			_albumProfile = result;
			_status.setText(tr::lng_musigram_source_hint(tr::now));
			renderTracks();
		}
	} else if (command == u"artistPage"_q || command == u"albumPage"_q) {
		const auto page = result.value(u"Page"_q).toObject();
		const auto album = command == u"albumPage"_q;
		const auto albums = result.value(u"Kind"_q).toString() == u"albums"_q;
		auto &profile = album ? _albumProfile : _artistProfile;
		const auto owner = album ? _albumId : _artistId;
		const auto cursor = album ? u"Continuation"_q : albums ? u"AlbumContinuation"_q : u"TrackContinuation"_q;
		if (owner == page.value(u"OwnerId"_q).toString() && !owner.isEmpty() && profile.value(cursor) == result.value(u"Previous"_q)) {
			const auto field = albums ? u"Albums"_q : u"Tracks"_q;
			auto items = profile.value(field).toArray();
			const auto key = albums ? u"Id"_q : u"Url"_q;
			auto keys = QSet<QString>();
			for (const auto &item : items) keys.insert(item.toObject().value(key).toString());
			for (const auto &item : page.value(field).toArray()) {
				if (!keys.contains(item.toObject().value(key).toString())) { items.push_back(item); keys.insert(item.toObject().value(key).toString()); }
			}
			profile.insert(field, items);
			profile.insert(cursor, page.value(u"Continuation"_q));
			_loadingPage = false;
			_status.setText(tr::lng_musigram_source_hint(tr::now));
			renderTracks();
		}
	} else if (command == u"favoriteArtist"_q) {
		if (_homeMode || isSearchLanding()) renderTracks();
	} else if (command == u"library"_q || command == u"playlistChange"_q || command == u"like"_q) {
		const auto change = result.value(u"Change"_q).toObject();
		if (!_pendingPlaylistId.isEmpty() && change.value(u"PlaylistId"_q).toString() == _pendingPlaylistId && change.value(u"Kind"_q).toString() == u"create"_q) {
			_playlistId = _pendingPlaylistId;
			_pendingPlaylistId.clear();
			_genreKey.clear();
			_playlistMode = true;
			_homeMode = false;
			_artistId.clear();
			_albumId.clear();
		}
		refreshPlaylistLabel();
		if (_playlistMode || _homeMode || isSearchLanding()) {
			renderTracks();
		}
		if (_playlistMode || command == u"playlistChange"_q || command == u"like"_q) _status.setText(tr::lng_musigram_synced(tr::now));
	}
}

bool CatalogWidget::isSearchLanding() const {
	return !_homeMode && !_playlistMode && !_soundCloudMode && _genreKey.isEmpty() && _artistId.isEmpty()
		&& _albumId.isEmpty() && _query.getLastText().trimmed().isEmpty();
}

void CatalogWidget::renderDiscovery() {
	const auto pad = st::musigramCatalogPadding;
	const auto gap = st::musigramCatalogGap;
	_rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_discovery_picks(), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
	const auto themes = std::vector<QString>{
		u"pop"_q, u"rock"_q, u"hiphop"_q, u"electronic"_q,
		u"focus"_q, u"evening"_q, u"indie"_q, u"energy"_q,
	};
	const auto titles = std::vector<QString>{
			tr::lng_musigram_genre_pop(tr::now), tr::lng_musigram_genre_rock(tr::now),
			tr::lng_musigram_genre_hiphop(tr::now), tr::lng_musigram_genre_electronic(tr::now),
			tr::lng_musigram_focus(tr::now), tr::lng_musigram_evening(tr::now),
			tr::lng_musigram_indie(tr::now), tr::lng_musigram_energy(tr::now),
	};
	_rows->add(object_ptr<DiscoveryGrid>(_rows, titles, std::vector<QString>(themes.size()),
		[=](int index) { openGenre(themes[index], titles[index]); },
		Fn<void(int, QPoint)>(), std::vector<bool>(), themes), { pad, gap, pad, gap });
	renderRecommendations();
	const auto choose = _rows->add(object_ptr<Ui::RoundButton>(_rows,
		rpl::single(u"Настроить музыкальные предпочтения"_q), st::defaultLightButton), { pad, gap, pad, gap });
	choose->setClickedCallback([=] { chooseTaste(); });
}

void CatalogWidget::renderTracks() {
    const auto oldScroll = _scroll.scrollTop();
    _shownTracks = _query.getLastText().trimmed().isEmpty() ? QJsonArray() : _backend->tracks();
    if (_playlistMode) {
        _shownTracks = {};
        for (const auto &value : _backend->playlists()) {
            const auto playlist = value.toObject();
            if (playlist.value(u"Id"_q).toString() == _playlistId) {
                _shownTracks = playlist.value(u"Tracks"_q).toArray();
                break;
            }
        }
    }
    if (_playlistMode) {
        const auto normalized = [](QString text) {
            return text.normalized(QString::NormalizationForm_KC)
                .toCaseFolded().replace(QChar(0x0451), QChar(0x0435));
        };
        const auto words = normalized(_playlistQuery.getLastText())
            .split(QRegularExpression(u"\\s+"_q), Qt::SkipEmptyParts);
        if (!words.isEmpty()) {
            auto matches = QJsonArray();
            for (const auto &value : _shownTracks) {
                const auto track = value.toObject();
                const auto text = normalized(track.value(u"Title"_q).toString()
                    + u" "_q + track.value(u"Artist"_q).toString()
                    + u" "_q + track.value(u"Album"_q).toString());
                if (ranges::all_of(words, [&](const QString &word) {
                    return text.contains(word);
                })) {
                    matches.push_back(value);
                }
            }
            _shownTracks = std::move(matches);
        }
    }
    if (_homeMode) _shownTracks = {};
    if (!_artistId.isEmpty()) _shownTracks = _artistProfile.value(u"Tracks"_q).toArray();
    if (!_artistId.isEmpty() && _artistAlbums) _shownTracks = {};
    if (!_albumId.isEmpty()) _shownTracks = _albumProfile.value(u"Tracks"_q).toArray();
    if (!_genreKey.isEmpty()) _shownTracks = _genreProfile.value(u"Tracks"_q).toArray();
    _heading.setText(_playlistMode ? tr::lng_musigram_collection(tr::now) : !_artistId.isEmpty() ? tr::lng_musigram_artist(tr::now) : _homeMode ? tr::lng_musigram_wave(tr::now) : tr::lng_musigram_results(tr::now));
    if (!_homeMode && !_playlistMode && _artistId.isEmpty() && _albumId.isEmpty() && _genreKey.isEmpty() && _soundCloudMode) _heading.setText(tr::lng_musigram_soundcloud_results(tr::now));
    if (!_genreKey.isEmpty()) _heading.setText(_genreTitle);
    _rows->clear();
    const auto pad = st::musigramCatalogPadding;
    const auto gap = st::musigramCatalogGap;
    if (!_genreKey.isEmpty()) {
        _rows->add(object_ptr<DiscoveryTile>(_rows, _genreTitle, QString(), false, 0, false, _genreKey), { pad, gap, pad, gap });
        if (!_shownTracks.isEmpty()) {
            const auto listen = _rows->add(object_ptr<Ui::RoundButton>(_rows, tr::lng_musigram_play(), st::defaultActiveButton), { pad, gap, pad, gap });
            listen->setClickedCallback([=] { if (!_shownTracks.isEmpty()) play(_shownTracks.first().toObject()); });
            _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(_genreProfile.value(u"SourcePlaylistName"_q).toString()), st::defaultSubTextLabel), { pad, gap, pad, gap });
        }
        if (_genreLoading) {
            _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_searching(), st::defaultSubTextLabel), { pad, gap, pad, gap });
        } else if (!_genreProfile.value(u"Error"_q).toString().isEmpty()) {
            const auto retry = _rows->add(object_ptr<Ui::RoundButton>(_rows, tr::lng_musigram_discovery_retry(), st::defaultLightButton), { pad, gap, pad, gap });
            retry->setClickedCallback([=] { openGenre(_genreKey, _genreTitle); });
        }
    } else if (!_albumId.isEmpty()) {
        _rows->add(object_ptr<ArtistBanner>(_rows, _backend, _albumProfile), { pad, gap, pad, gap });
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_tracks(), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
    } else if (!_artistId.isEmpty()) {
        _rows->add(object_ptr<ArtistBanner>(_rows, _backend, _artistProfile), { pad, gap, pad, gap });
        const auto tracks = _artistProfile.value(u"Tracks"_q).toArray();
        const auto albums = _artistProfile.value(u"Albums"_q).toArray();
        const auto trackLabel = tr::lng_musigram_tracks(tr::now) + u" · "_q + QString::number(tracks.size()) + (_artistProfile.value(u"TrackContinuation"_q).toString().isEmpty() ? QString() : u"+"_q);
        const auto albumLabel = tr::lng_musigram_albums(tr::now) + u" · "_q + QString::number(albums.size()) + (_artistProfile.value(u"AlbumContinuation"_q).toString().isEmpty() ? QString() : u"+"_q);
        _rows->add(object_ptr<CatalogTabs>(_rows, _artistAlbums, trackLabel, albumLabel, [=](bool albums) { _artistAlbums = albums; renderTracks(); }), { pad, gap, pad, gap });
        if (_artistAlbums) {
            _rows->add(object_ptr<ArtistGrid>(_rows, _backend, albums, [=](QJsonObject album) { openAlbum(album); }, true), { pad, gap, pad, gap });
            if (albums.isEmpty()) _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_no_albums(), st::defaultSubTextLabel), { pad, gap, pad, gap });
        }
    } else if (_playlistMode && _playlistId.isEmpty() && !_backend->playlists().isEmpty()) {
        const auto playlists = _backend->playlists();
        auto titles = std::vector<QString>();
        auto subtitles = std::vector<QString>();
        auto favorites = std::vector<bool>();
        for (const auto &value : playlists) {
            const auto playlist = value.toObject();
            titles.push_back(playlist.value(u"Name"_q).toString());
            subtitles.push_back(tr::lng_musigram_tracks(tr::now) + u": "_q + QString::number(playlist.value(u"Tracks"_q).toArray().size()));
            favorites.push_back(playlist.value(u"IsFavorites"_q).toBool());
        }
        _rows->add(object_ptr<DiscoveryGrid>(_rows, std::move(titles), std::move(subtitles), [=](int index) {
            _suggestTimer.cancel();
            _playlistQuery.setText(QString());
            _playlistId = playlists[index].toObject().value(u"Id"_q).toString();
            refreshPlaylistLabel();
            renderTracks();
        }, [=](int index, QPoint point) {
            if (playlists[index].toObject().value(u"IsFavorites"_q).toBool()) return;
            _playlistId = playlists[index].toObject().value(u"Id"_q).toString();
            _menu = base::make_unique_q<Ui::PopupMenu>(this);
            _menu->addAction(tr::lng_musigram_rename(tr::now), [=] { playlistNameBox(true); });
            _menu->addAction(tr::lng_musigram_delete(tr::now), [=] { deletePlaylist(); });
            _menu->popup(point);
        }, std::move(favorites)), { pad, gap, pad, gap });
    } else if (_homeMode) {
        auto holder = std::make_shared<QPointer<PromotionBanner>>();
        auto promotion = object_ptr<PromotionBanner>(_rows, [holder] {
            if (const auto card = holder->data()) {
                card->resizeToWidth(card->width());
                card->setVisible(card->available());
            }
        });
        *holder = promotion.data();
        _rows->add(std::move(promotion), { pad, gap, pad, gap });
        _rows->add(object_ptr<WavePanel>(_rows), { pad, gap, pad, gap });
        const auto favorites = _backend->favoriteArtists();
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_favorite_artists(), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
        if (!favorites.isEmpty()) {
            _rows->add(object_ptr<ArtistGrid>(_rows, _backend, favorites, [=](QJsonObject artist) { openArtist(artist); }), { pad, gap, pad, gap });
        }
        const auto choose = _rows->add(object_ptr<Ui::RoundButton>(_rows, tr::lng_musigram_choose_artists(), st::defaultLightButton), { pad, gap, pad, gap });
        choose->setClickedCallback([=] { chooseTaste(); });
        renderRecommendations();
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_moods(), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
        const auto themes = std::vector<QString>{ u"focus"_q, u"evening"_q, u"indie"_q, u"energy"_q };
        const auto titles = std::vector<QString>{ tr::lng_musigram_focus(tr::now), tr::lng_musigram_evening(tr::now), tr::lng_musigram_indie(tr::now), tr::lng_musigram_energy(tr::now) };
        _rows->add(object_ptr<DiscoveryGrid>(_rows,
            titles, std::vector<QString>(themes.size()), [=](int index) { openGenre(themes[index], titles[index]); },
            Fn<void(int, QPoint)>(), std::vector<bool>(), themes), { pad, gap, pad, gap });
    } else if (isSearchLanding()) {
        renderDiscovery();
    } else if (_playlistMode && _shownTracks.isEmpty()) {
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, (_playlistQuery.getLastText().trimmed().isEmpty()
            ? tr::lng_musigram_playlist_empty()
            : tr::lng_musigram_empty()), st::defaultSubTextLabel), { pad, gap, pad, gap });
    }
    if (!_playlistMode && !_homeMode && _genreKey.isEmpty() && _artistId.isEmpty() && _albumId.isEmpty() && !isSearchLanding() && !_query.getLastText().trimmed().isEmpty()) {
        if (!_artists.isEmpty()) {
            _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_artists(), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
            _rows->add(object_ptr<ArtistGrid>(_rows, _backend, _artists, [=](QJsonObject artist) { openArtist(artist); }), { pad, gap, pad, gap });
        }
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, tr::lng_musigram_tracks(), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
    }
    if (!_shownTracks.isEmpty()) {
        _rows->add(object_ptr<TrackList>(_rows, _backend, _shownTracks,
            [=](QJsonObject track) { play(track); }, [=](QJsonObject track) { addToPlaylist(track); },
            [=](QJsonObject track, QPoint point) { trackMenu(track, point); }));
    }
    const auto continuation = !_genreKey.isEmpty() ? _genreProfile.value(u"Continuation"_q).toString() : !_albumId.isEmpty() ? _albumProfile.value(u"Continuation"_q).toString() : !_artistId.isEmpty() ? _artistProfile.value(_artistAlbums ? u"AlbumContinuation"_q : u"TrackContinuation"_q).toString() : QString();
    if (!continuation.isEmpty()) {
        const auto more = _rows->add(object_ptr<Ui::RoundButton>(_rows, _loadingPage ? tr::lng_musigram_searching() : tr::lng_musigram_more(), st::defaultLightButton), { pad, gap, pad, gap });
        more->setClickedCallback([=] { loadMore(); });
    }
    layout();
    _scroll.scrollToY(oldScroll);
}

void CatalogWidget::showHome() {
	_genreKey.clear();
	_suggestTimer.cancel();
	_homeMode = true;
	_playlistMode = false;
	_artistId.clear();
	_albumId.clear();
	_status.setText(tr::lng_musigram_source_hint(tr::now));
	renderTracks();
	_scroll.scrollToY(0);
}

QString CatalogWidget::tasteKey() const {
    auto ids = QStringList();
    for (const auto &item : _backend->favoriteArtists()) ids.push_back(item.toObject().value(u"Id"_q).toString());
    ids.removeDuplicates(); ids.sort();
    return ids.join(u'|');
}

void CatalogWidget::requestRecommendations(bool refresh) {
    if (!_tasteReady || !_tasteCompleted || _backend->favoriteArtists().size() < 5) return;
    const auto key = tasteKey();
    if (!refresh && !_recommendationError.isEmpty() && _recommendationPendingSeed == key) return;
    if (!refresh && ((_recommendations.value(u"SeedKey"_q).toString() == key)
        || (_recommendationLoading && _recommendationPendingSeed == key))) return;
    _recommendationLoading = true;
    _recommendationError.clear();
    _recommendationPendingSeed = key;
    _recommendationRequestKey = QUuid::createUuid().toString(QUuid::Id128);
    _backend->send({ { u"command"_q, u"recommendations"_q }, { u"requestKey"_q, _recommendationRequestKey }, { u"refresh"_q, refresh } });
}

void CatalogWidget::chooseTaste() {
    if (_tasteBox) return;
    const auto backend = _backend;
    const auto widget = QPointer<CatalogWidget>(this);
    controller()->show(Box([=](not_null<Ui::GenericBox*> box) {
        _tasteBox = box;
        box->setTitle(rpl::single(u"Выберите любимых исполнителей"_q));
        box->setWidth(st::boxWideWidth + st::boxWidth / 2);
        box->setMaxHeight(st::boxMaxListHeight);
        auto top = object_ptr<Ui::VerticalLayout>(box);
        top->add(object_ptr<Ui::FlatLabel>(top, rpl::single(u"Выберите минимум пять. По ним MeloDesk подберёт альбомы и похожих артистов. Выбор можно изменить позже."_q), st::defaultFlatLabel), st::boxRowPadding);
        const auto field = top->add(object_ptr<Ui::InputField>(top, st::defaultInputField, rpl::single(u"Найти исполнителя"_q)), st::boxRowPadding);
        struct State {
            QTimer timer;
            QString query;
            QSet<QString> pending;
            rpl::variable<QString> status = QString();
            rpl::variable<QString> selected = QString();
            Ui::RoundButton *done = nullptr;
            bool saving = false;
        };
        const auto state = box->lifetime().make_state<State>();
        top->add(object_ptr<Ui::FlatLabel>(top, state->status.value(), st::defaultFlatLabel), st::boxRowPadding);
        top->add(object_ptr<Ui::FlatLabel>(top, state->selected.value(), st::defaultSubTextLabel), st::boxRowPadding);
        box->setPinnedToTopContent(std::move(top));
        const auto rows = box->addRow(object_ptr<Ui::VerticalLayout>(box), style::margins());
        const auto update = [=] {
            const auto favorites = backend->favoriteArtists();
            state->status = u"Выбрано: "_q + QString::number(favorites.size()) + u" · нужно минимум 5"_q;
            auto names = QStringList();
            for (const auto &item : favorites) names.push_back(item.toObject().value(u"Name"_q).toString());
            state->selected = names.mid(0, 10).join(u", "_q) + (names.size() > 10 ? u"…"_q : QString());
            if (state->done) state->done->setDisabled(favorites.size() < 5 || state->saving || !state->pending.isEmpty());
        };
        const auto render = [=](QJsonArray artists) {
            rows->clear();
            if (artists.isEmpty()) {
                rows->add(object_ptr<Ui::FlatLabel>(rows, rpl::single(u"Исполнители не найдены. Попробуйте другое имя или проверьте подключение."_q), st::defaultSubTextLabel), st::boxRowPadding);
                return;
            }
            rows->add(object_ptr<ArtistGrid>(rows, backend, artists, [=](QJsonObject artist) {
                if (!widget) return;
                const auto id = artist.value(u"Id"_q).toString();
                if (state->pending.contains(id)) return;
                state->pending.insert(id); update();
                backend->send({ { u"command"_q, u"favoriteArtist"_q }, { u"artist"_q, artist }, { u"favorite"_q, !backend->isFavoriteArtist(id) } });
            }), st::boxRowPadding);
        };
        state->done = box->addButton(rpl::single(u"Готово"_q), [=] {
            if (!widget) return;
            if (backend->favoriteArtists().size() < 5 || state->saving || !state->pending.isEmpty()) return;
            state->saving = true; update();
            backend->send({ { u"command"_q, u"completeTaste"_q } });
        });
        box->addButton(rpl::single(u"Позже"_q), [=] { box->closeBox(); });
        update();
        if (!backend->favoriteArtists().isEmpty()) render(backend->favoriteArtists());
        else rows->add(object_ptr<Ui::FlatLabel>(rows, rpl::single(u"Загружаем исполнителей…"_q), st::defaultSubTextLabel), st::boxRowPadding);
        backend->messages() | rpl::on_next([=](const QJsonObject &message) {
            if (!widget) return;
            const auto command = message.value(u"Command"_q).toString();
            const auto result = message.value(u"Result"_q).toObject();
            if (command == u"artistChoices"_q) {
                if (message.value(u"Cancelled"_q).toBool()) return;
                if (!message.value(u"Error"_q).toString().isEmpty()) { state->status = message.value(u"Error"_q).toString(); return; }
                if (result.value(u"Query"_q).toString() == field->getLastText().trimmed()) {
                    auto artists = result.value(u"Artists"_q).toArray();
                    if (field->getLastText().trimmed().isEmpty()) {
                        auto combined = QJsonArray(); auto ids = QSet<QString>();
                        const auto add = [&](const QJsonValue &item) {
                            const auto id = item.toObject().value(u"Id"_q).toString();
                            if (!ids.contains(id)) { ids.insert(id); combined.push_back(item); }
                        };
                        const auto favorites = backend->favoriteArtists();
                        for (auto i = 0; i < std::min(12, int(favorites.size())); ++i) add(favorites[i]);
                        for (const auto &artist : artists) add(artist);
                        artists = combined;
                    }
                    render(artists);
                }
            } else if (command == u"favoriteArtist"_q) {
                state->pending.clear(); update();
                if (!message.value(u"Error"_q).toString().isEmpty()) state->status = message.value(u"Error"_q).toString();
            } else if (command == u"completeTaste"_q && state->saving) {
                state->saving = false;
                if (message.value(u"Ok"_q).toBool()) box->closeBox();
                else { update(); state->status = message.value(u"Error"_q).toString(); }
            }
        }, box->lifetime());
        state->timer.setSingleShot(true);
        QObject::connect(&state->timer, &QTimer::timeout, box, [=] {
            if (!widget) return;
            state->query = field->getLastText().trimmed();
            backend->send({ { u"command"_q, u"artistChoices"_q }, { u"query"_q, state->query } });
        });
        field->changes() | rpl::on_next([=] { state->timer.start(400); }, box->lifetime());
        box->setFocusCallback([=] { field->setFocus(); });
        backend->send({ { u"command"_q, u"artistChoices"_q }, { u"query"_q, QString() } });
    }));
}

void CatalogWidget::renderRecommendations() {
    const auto pad = st::musigramCatalogPadding;
    const auto gap = st::musigramCatalogGap;
    if (!_tasteReady) return;
    if (!_tasteCompleted || _backend->favoriteArtists().size() < 5) {
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(u"Для рекомендаций выберите минимум пять любимых исполнителей."_q), st::defaultSubTextLabel), { pad, gap, pad, gap });
        return;
    }
    requestRecommendations();
    _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(isSearchLanding() ? u"Вам могут понравиться"_q : u"Для вас"_q), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
    auto seeds = QStringList();
    for (const auto &item : _backend->favoriteArtists()) seeds.push_back(item.toObject().value(u"Name"_q).toString());
    _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(u"На основе: "_q + seeds.mid(0, 8).join(u", "_q)), st::defaultSubTextLabel), { pad, gap, pad, gap });
    const auto feed = _recommendations.value(u"SeedKey"_q).toString() == tasteKey() ? _recommendations : QJsonObject();
    const auto albums = feed.value(u"Albums"_q).toArray();
    const auto artists = feed.value(u"Artists"_q).toArray();
    if (!albums.isEmpty()) {
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(u"Альбомы для вас"_q), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
        _rows->add(object_ptr<ArtistGrid>(_rows, _backend, albums, [=](QJsonObject album) { openAlbum(album); }, true, false), { pad, gap, pad, gap });
    }
    if (!artists.isEmpty()) {
        _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(isSearchLanding() ? u"Исполнители по вашему вкусу"_q : u"Вам могут понравиться"_q), st::musigramCatalogHeadingLabel), { pad, gap, pad, gap });
        _rows->add(object_ptr<ArtistGrid>(_rows, _backend, artists, [=](QJsonObject artist) { openArtist(artist); }), { pad, gap, pad, gap });
    }
    auto message = _recommendationLoading ? u"Подбираем музыку по вашим исполнителям…"_q : _recommendationError;
    for (const auto &item : feed.value(u"Messages"_q).toArray()) { if (!message.isEmpty()) message += u'\n'; message += item.toString(); }
    if (!message.isEmpty()) _rows->add(object_ptr<Ui::FlatLabel>(_rows, rpl::single(message), st::defaultSubTextLabel), { pad, gap, pad, gap });
    const auto refresh = _rows->add(object_ptr<Ui::RoundButton>(_rows, rpl::single(u"Обновить подборку"_q), st::defaultLightButton), { pad, gap, pad, gap });
    refresh->setDisabled(_recommendationLoading);
    refresh->setClickedCallback([=] { requestRecommendations(true); renderTracks(); });
}

void CatalogWidget::openArtist(QJsonObject artist) {
	_genreKey.clear();
	_suggestTimer.cancel();
	_artistFromHome = _homeMode;
	_artistId = artist.value(u"Id"_q).toString();
	if (_artistId.isEmpty()) return;
	_homeMode = false;
	_playlistMode = false;
	_artistProfile = artist;
	_albumId.clear();
	_artistAlbums = false;
	_loadingPage = false;
	_status.setText(tr::lng_musigram_artist_loading(tr::now));
	renderTracks();
	_scroll.scrollToY(0);
	_backend->send({ { u"command"_q, u"artist"_q }, { u"artistId"_q, _artistId } });
}

void CatalogWidget::openAlbum(QJsonObject album) {
	_genreKey.clear();
	_suggestTimer.cancel();
	_albumId = album.value(u"Id"_q).toString();
	_albumProfile = album;
	_loadingPage = false;
	_status.setText(tr::lng_musigram_album_loading(tr::now));
	renderTracks();
	_scroll.scrollToY(0);
	_backend->send({ { u"command"_q, u"album"_q }, { u"albumId"_q, _albumId } });
}

void CatalogWidget::openGenre(QString key, QString title) {
	_suggestTimer.cancel();
	if (_genreKey.isEmpty()) _genreFromHome = _homeMode;
	_genreKey = std::move(key);
	_genreTitle = std::move(title);
	_genreProfile = {};
	_genreLoading = true;
	_loadingPage = false;
	_homeMode = false;
	_playlistMode = false;
	_artistId.clear();
	_albumId.clear();
	_status.setText(tr::lng_musigram_searching(tr::now));
	renderTracks();
	_scroll.scrollToY(0);
	_backend->send({ { u"command"_q, u"genre"_q }, { u"key"_q, _genreKey } });
}

void CatalogWidget::loadMore() {
	if (_loadingPage) return;
	const auto continuation = !_genreKey.isEmpty() ? _genreProfile.value(u"Continuation"_q).toString() : !_albumId.isEmpty() ? _albumProfile.value(u"Continuation"_q).toString() : _artistProfile.value(_artistAlbums ? u"AlbumContinuation"_q : u"TrackContinuation"_q).toString();
	if (continuation.isEmpty()) return;
	_loadingPage = true;
	_status.setText(tr::lng_musigram_searching(tr::now));
	if (!_genreKey.isEmpty()) _backend->send({ { u"command"_q, u"genrePage"_q }, { u"key"_q, _genreKey }, { u"continuation"_q, continuation } });
	else if (!_albumId.isEmpty()) _backend->send({ { u"command"_q, u"albumPage"_q }, { u"albumId"_q, _albumId }, { u"continuation"_q, continuation } });
	else _backend->send({ { u"command"_q, u"artistPage"_q }, { u"artistId"_q, _artistId }, { u"kind"_q, _artistAlbums ? u"albums"_q : u"tracks"_q }, { u"continuation"_q, continuation } });
}

void CatalogWidget::choosePlaylist() {
    _suggestTimer.cancel();
    _playlistQuery.setText(QString());
	_genreKey.clear();
	_suggestTimer.cancel();
    _playlistMode = true;
    _homeMode = false;
    _artistId.clear();
    _albumId.clear();
    _playlistId.clear();
    refreshPlaylistLabel();
    renderTracks();
}

void CatalogWidget::refreshPlaylistLabel() {
	for (const auto &value : _backend->playlists()) {
		const auto playlist = value.toObject();
		if (playlist.value(u"Id"_q).toString() == _playlistId) {
			_playlist.setText(rpl::single(playlist.value(u"Name"_q).toString()));
			return;
		}
	}
	_playlistId.clear();
	_playlist.setText(tr::lng_musigram_collection());
}

void CatalogWidget::playlistNameBox(bool rename, QJsonObject track) {
	controller()->show(Box([=](not_null<Ui::GenericBox*> box) {
		box->setTitle(rename ? tr::lng_musigram_rename() : tr::lng_musigram_create());
		const auto field = box->addRow(object_ptr<Ui::InputField>(
			box,
			st::defaultInputField,
			tr::lng_musigram_playlist_name()));
		box->setFocusCallback([=] { field->setFocus(); });
		box->addButton(tr::lng_settings_save(), crl::guard(this, [=] {
			const auto name = field->getLastText().trimmed();
			if (name.isEmpty() || name.size() > 100) {
				field->showError();
				return;
			}
			const auto id = rename ? _playlistId : QUuid::createUuid().toString(QUuid::WithoutBraces);
			if (!rename) _pendingPlaylistId = id;
			playlistChange({
				{ u"kind"_q, rename ? u"rename"_q : u"create"_q },
				{ u"playlistId"_q, id },
				{ u"name"_q, name },
				{ u"track"_q, track.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(track) },
			});
			box->closeBox();
		}));
		box->addButton(tr::lng_cancel(), [=] { box->closeBox(); });
	}));
}

void CatalogWidget::addContentMenu() {
	_menu = base::make_unique_q<Ui::PopupMenu>(this);
	_menu->addAction(tr::lng_musigram_transfer_title(tr::now), [=] { Ui::PostponeCall(this, [=] { importBox(); }); });
	_menu->addAction(tr::lng_musigram_local_files(tr::now), [=] { Ui::PostponeCall(this, [=] { addLocalFiles(); }); });
	_menu->popup(QCursor::pos());
}

void CatalogWidget::addLocalFiles() {
	const auto playlist = _playlistMode ? _playlistId : QString();
	FileDialog::GetOpenPaths(this, tr::lng_musigram_local_files(tr::now),
		u"Audio (*.mp3 *.flac *.m4a *.ogg *.opus *.wav *.aac *.wma)"_q,
		crl::guard(this, [=](FileDialog::OpenResult &&result) {
			if (result.paths.isEmpty()) return;
			auto paths = QJsonArray();
			for (const auto &path : result.paths) paths.push_back(path);
			_status.setText(tr::lng_musigram_local_loading(tr::now));
			_backend->send({ { u"command"_q, u"localAdd"_q }, { u"paths"_q, paths }, { u"playlistId"_q, playlist } });
		}));
}

void CatalogWidget::importBox() {
	if (_transferBox) return;
	controller()->show(Box([=](not_null<Ui::GenericBox*> box) {
		_transferBox = box;
		_transferPreview = {};
		_transferPage = 0;
		_transferBusy = false;
		box->setTitle(tr::lng_musigram_transfer_title());
		box->setWidth(st::musigramTransferWidth);
		box->setMaxHeight(st::musigramTransferHeight);
		box->addRow(object_ptr<Ui::FlatLabel>(box, tr::lng_musigram_transfer_about(), st::defaultSubTextLabel));
		_transferInput = box->addRow(object_ptr<Ui::InputField>(box, st::defaultInputField, tr::lng_musigram_transfer_link()));
		const auto open = box->addRow(object_ptr<Ui::RoundButton>(box, tr::lng_musigram_transfer_open(), st::defaultActiveButton));
		open->setClickedCallback([=] {
			if (_transferBusy) return;
			const auto input = _transferInput->getLastText().trimmed();
			if (input.isEmpty()) { _transferInput->showError(); return; }
			_transferBusy = true;
			_transferSummary->setText(tr::lng_musigram_searching(tr::now));
			_backend->send({ { u"command"_q, u"importPreview"_q }, { u"input"_q, input }, { u"file"_q, false } });
		});
		const auto file = box->addRow(object_ptr<Ui::RoundButton>(box, tr::lng_musigram_transfer_file(), st::defaultLightButton));
		file->setClickedCallback([=] {
			if (_transferBusy) return;
			FileDialog::GetOpenPaths(this, tr::lng_musigram_transfer_file(tr::now), u"Playlists (*.csv *.json)"_q,
				crl::guard(this, [=](FileDialog::OpenResult &&result) {
					if (!_transferBox || result.paths.isEmpty()) return;
					_transferBusy = true;
					_transferSummary->setText(tr::lng_musigram_searching(tr::now));
					_backend->send({ { u"command"_q, u"importPreview"_q }, { u"input"_q, result.paths.front() }, { u"file"_q, true } });
				}));
		});
		_transferName = box->addRow(object_ptr<Ui::InputField>(box, st::defaultInputField, tr::lng_musigram_playlist_name()));
		_transferSummary = box->addRow(object_ptr<Ui::FlatLabel>(box, tr::lng_musigram_transfer_hint(), st::defaultSubTextLabel));
		_transferRows = box->addRow(object_ptr<Ui::VerticalLayout>(box));
		box->addButton(tr::lng_musigram_transfer_save(), [=] {
			if (!_transferBox || _transferBusy) return;
			const auto name = _transferName->getLastText().trimmed();
			if (name.isEmpty() || name.size() > 100) { _transferName->showError(); return; }
			if (_transferPreview.value(u"Matched"_q).toInt() <= 0) return;
			_transferBusy = true;
			_backend->send({ { u"command"_q, u"importCommit"_q }, { u"previewId"_q, _transferPreview.value(u"Id"_q) }, { u"name"_q, name } });
		});
		box->addButton(tr::lng_cancel(), [=] { box->closeBox(); });
		box->boxClosing() | rpl::on_next([=] {
			_backend->send({ { u"command"_q, u"importCancel"_q } });
			_transferBox = nullptr;
		}, box->lifetime());
	}));
}

void CatalogWidget::receiveImport(const QJsonObject &message) {
	if (!_transferBox) return;
	if (message.value(u"Event"_q).toString() == u"importProgress"_q) {
		if (message.value(u"PreviewId"_q) == _transferPreview.value(u"Id"_q)) {
			_transferSummary->setText(QString::number(message.value(u"Done"_q).toInt()) + u" / "_q + QString::number(message.value(u"Total"_q).toInt()));
		}
		return;
	}
	_transferBusy = false;
	const auto error = message.value(u"Error"_q).toString();
	if (!error.isEmpty()) { _transferSummary->setText(error); return; }
	const auto command = message.value(u"Command"_q).toString();
	const auto result = message.value(u"Result"_q).toObject();
	if (command == u"importCommit"_q) {
		_playlistId = result.value(u"PlaylistId"_q).toString();
		_playlistMode = true; _homeMode = false;
		_genreKey.clear(); _artistId.clear(); _albumId.clear();
		_transferBox->closeBox();
		refreshPlaylistLabel(); renderTracks();
		_status.setText(tr::lng_musigram_transfer_done(tr::now) + u" "_q
			+ QString::number(result.value(u"Added"_q).toInt()) + u" · "_q
			+ tr::lng_musigram_transfer_unresolved(tr::now) + u" "_q + QString::number(result.value(u"Unresolved"_q).toInt()));
	} else if (command == u"importPreview"_q || command == u"importResolve"_q || command == u"importChoice"_q) {
		if (message.value(u"Cancelled"_q).toBool()) return;
		_transferPreview = result;
		if (command == u"importPreview"_q) { _transferName->setText(result.value(u"Name"_q).toString()); _transferPage = 0; }
		renderImport();
	}
}

void CatalogWidget::renderImport() {
	if (!_transferBox) return;
	_transferRows->clear();
	const auto entries = _transferPreview.value(u"Entries"_q).toArray();
	_transferSummary->setText(tr::lng_musigram_transfer_ready(tr::now) + u" "_q
		+ QString::number(_transferPreview.value(u"Matched"_q).toInt()) + u" / "_q + QString::number(entries.size()));
	const auto start = _transferPage * st::musigramTransferPageSize;
	const auto end = std::min(int(entries.size()), start + st::musigramTransferPageSize);
	for (auto index = start; index < end; ++index) {
		const auto entry = entries[index].toObject();
		const auto text = entry.value(u"Title"_q).toString() + u" · "_q + entry.value(u"Artist"_q).toString() + u" — "_q + entry.value(u"Status"_q).toString();
		const auto choices = entry.value(u"Candidates"_q).toArray();
		if (choices.isEmpty()) {
			_transferRows->add(object_ptr<Ui::FlatLabel>(_transferRows, rpl::single(text), st::defaultSubTextLabel), st::boxRowPadding);
		} else {
			const auto choose = _transferRows->add(object_ptr<Ui::RoundButton>(_transferRows, rpl::single(text), st::defaultLightButton));
			choose->setClickedCallback([=] {
				if (_transferBusy) return;
				_menu = base::make_unique_q<Ui::PopupMenu>(this);
				for (const auto &value : choices) {
					const auto track = value.toObject();
					_menu->addAction(track.value(u"Title"_q).toString() + u" · "_q + track.value(u"Artist"_q).toString(), [=] {
						_backend->send({ { u"command"_q, u"importChoice"_q }, { u"previewId"_q, _transferPreview.value(u"Id"_q) }, { u"index"_q, index }, { u"url"_q, track.value(u"Url"_q) } });
					});
				}
				_menu->popup(QCursor::pos());
			});
		}
	}
	if (end < entries.size()) {
		const auto next = _transferRows->add(object_ptr<Ui::RoundButton>(_transferRows, tr::lng_musigram_more(), st::defaultLightButton));
		next->setClickedCallback([=] { ++_transferPage; renderImport(); });
	}
	if (_transferPage > 0) {
		const auto previous = _transferRows->add(object_ptr<Ui::RoundButton>(_transferRows, tr::lng_musigram_transfer_previous(), st::defaultLightButton));
		previous->setClickedCallback([=] { --_transferPage; renderImport(); });
	}
	if (_transferPreview.value(u"Unresolved"_q).toInt() > 0) {
		const auto resolve = _transferRows->add(object_ptr<Ui::RoundButton>(_transferRows, tr::lng_musigram_transfer_resolve(), st::defaultActiveButton));
		resolve->setClickedCallback([=] {
			if (_transferBusy) return;
			_transferBusy = true;
			_backend->send({ { u"command"_q, u"importResolve"_q }, { u"previewId"_q, _transferPreview.value(u"Id"_q) } });
		});
	}
}

void CatalogWidget::deletePlaylist() {
	const auto id = _playlistId;
	controller()->show(Ui::MakeConfirmBox({
		.text = tr::lng_musigram_delete_sure(tr::now),
		.confirmed = crl::guard(this, [=] {
			playlistChange({ { u"kind"_q, u"delete"_q }, { u"playlistId"_q, id } });
		}),
		.confirmText = tr::lng_musigram_delete(tr::now),
	}));
}

void CatalogWidget::trackMenu(QJsonObject track, QPoint position) {
	_menu = base::make_unique_q<Ui::PopupMenu>(this);
	_menu->addAction(tr::lng_musigram_play(tr::now), [=] { play(track); });
	if (!track.value(u"ArtistId"_q).toString().isEmpty()) {
		_menu->addAction(tr::lng_musigram_artist(tr::now), [=] {
			openArtist({ { u"Id"_q, track.value(u"ArtistId"_q) }, { u"Name"_q, track.value(u"Artist"_q) }, { u"Thumbnail"_q, track.value(u"Thumbnail"_q) } });
		});
	}
	_menu->addAction(tr::lng_musigram_add(tr::now), [=] {
		Ui::PostponeCall(this, [=] { addToPlaylist(track); });
	});
	if (_playlistMode && !_playlistId.isEmpty()) {
		_menu->addAction(tr::lng_musigram_remove(tr::now), [=] {
			playlistChange({
				{ u"kind"_q, u"remove"_q },
				{ u"playlistId"_q, _playlistId },
				{ u"trackUrl"_q, track.value(u"Url"_q) },
			});
		});
	}
	_menu->popup(position);
}

void CatalogWidget::addToPlaylist(QJsonObject track) {
	_menu = base::make_unique_q<Ui::PopupMenu>(this);
	_menu->addAction(tr::lng_musigram_create(tr::now), [=] { Ui::PostponeCall(this, [=] { playlistNameBox(false, track); }); });
	for (const auto &value : _backend->playlists()) {
		const auto playlist = value.toObject();
		_menu->addAction(playlist.value(u"Name"_q).toString(), [=] {
			playlistChange({
				{ u"kind"_q, u"add"_q },
				{ u"playlistId"_q, playlist.value(u"Id"_q) },
				{ u"track"_q, track },
			});
		});
	}
	_menu->popup(QCursor::pos());
}

void CatalogWidget::play(QJsonObject track) {
	if (_backend->playback().value(u"Track"_q).toObject().value(u"Url"_q) != track.value(u"Url"_q)) {
		_backend->send({ { u"command"_q, u"queue"_q }, { u"tracks"_q, _shownTracks }, { u"startUrl"_q, track.value(u"Url"_q) } });
	}
	_backend->send({ { u"command"_q, u"select"_q }, { u"track"_q, track }, { u"toggle"_q, true } });
}

void CatalogWidget::playlistChange(QJsonObject request) {
	request.insert(u"command"_q, u"playlistChange"_q);
	_backend->send(std::move(request));
}

void CatalogWidget::updatePlayback(const QJsonObject &snapshot) {
    const auto error = snapshot.value(u"Error"_q).toString();
    if (!error.isEmpty()) {
        _status.setText(error);
    }
}

std::shared_ptr<Window::SectionMemento> MakeArtistMemento(QJsonObject artist) {
	return std::make_shared<CatalogMemento>(std::move(artist));
}

} // namespace

std::shared_ptr<Window::SectionMemento> MakeCatalogMusic() {
	return std::make_shared<CatalogMemento>();
}

object_ptr<Ui::RpWidget> MakePromotionCard(
        QWidget *parent,
        Fn<void(bool)> visibilityChanged) {
    auto holder = std::make_shared<QPointer<PromotionBanner>>();
    auto card = object_ptr<PromotionBanner>(parent, [holder, changed = std::move(visibilityChanged)] {
        if (*holder) changed((*holder)->available());
    });
    *holder = card.data();
    return card;
}

object_ptr<Ui::RpWidget> MakeCatalogBar(
        QWidget *parent,
        not_null<Window::SessionController*> controller,
        Fn<void(bool)> visibilityChanged) {
    return object_ptr<CatalogBar>(parent, controller, std::move(visibilityChanged));
}

} // namespace Info::Saved
