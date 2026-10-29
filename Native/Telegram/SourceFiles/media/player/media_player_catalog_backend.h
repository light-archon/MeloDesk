#pragma once

#include <QtCore/QJsonArray>
#include <QtCore/QJsonObject>
#include <QtCore/QHash>
#include <QtCore/QProcess>
#include <QtCore/QTimer>
#include <QtCore/QCache>
#include <QtCore/QSet>
#include <QtGui/QImage>
#include <QtNetwork/QNetworkAccessManager>
#include <rpl/event_stream.h>

namespace Main {
class Session;
} // namespace Main

namespace Media::Player {

class CatalogBackend final : public QObject {
public:
	explicit CatalogBackend(not_null<Main::Session*> session);
	~CatalogBackend();
	void send(QJsonObject request);
	[[nodiscard]] rpl::producer<QJsonObject> messages() const;
	[[nodiscard]] QJsonArray tracks() const;
	[[nodiscard]] QJsonArray playlists() const;
	[[nodiscard]] QJsonObject playback() const;
	[[nodiscard]] QJsonObject controls() const;
	[[nodiscard]] QImage cover(const QString &url);
	[[nodiscard]] bool isLiked(const QString &url) const;
	[[nodiscard]] QJsonArray favoriteArtists() const;
	[[nodiscard]] bool isFavoriteArtist(const QString &id) const;
	void registerSection(bool opened);
	[[nodiscard]] bool sectionVisible() const;

private:
	void start();
	void read();
	void receive(QJsonObject message);
	void applyLibraryChange(const QJsonObject &change);
	void flush();
	void error(const QString &text);

	const not_null<Main::Session*> _session;
	QProcess _process;
	QTimer _timer;
	QNetworkAccessManager _network;
	QCache<QString, QImage> _covers{ 256 };
	QSet<QString> _coverJobs;
	QSet<QString> _likedUrls;
	QByteArray _input;
	QList<QJsonObject> _pending;
	QHash<QString, QString> _commands;
	QString _lastSearchId;
	QString _lastGenreId;
	QString _lastImportId;
	QJsonArray _tracks;
	QJsonArray _playlists;
	QJsonArray _favoriteArtists;
	QJsonObject _playback;
	QJsonObject _controls;
	rpl::event_stream<QJsonObject> _messages;
	int _serial = 0;
	qint64 _lastPlaybackRequestId = 0;
	bool _ready = false;
	bool _parsing = false;
	int _processGeneration = 0;
	int _sections = 0;

};

} // namespace Media::Player
