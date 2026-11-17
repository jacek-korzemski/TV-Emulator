// Frameless QtWebKit 5.5 host for the Tizen 2.4 profile.
// AppleWebKit/538.1, raster backing store (color key needs plain pixels), no chrome.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <QApplication>
#include <QContextMenuEvent>
#include <QDir>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonValue>
#include <QKeyEvent>
#include <QNetworkAccessManager>
#include <QNetworkCookie>
#include <QNetworkCookieJar>
#include <QNetworkDiskCache>
#include <QNetworkProxy>
#include <QNetworkReply>
#include <QSet>
#include <QSslError>
#include <QSslSocket>
#include <QTimer>
#include <QUrl>
#include <QWebFrame>
#include <QWebPage>
#include <QWebSettings>
#include <QWebView>
#include <QWidget>

namespace {

void writeOut(const QString &line)
{
    const QByteArray bytes = (line + QLatin1Char('\n')).toUtf8();
    HANDLE handle = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!handle || handle == INVALID_HANDLE_VALUE)
        return;
    DWORD written = 0;
    WriteFile(handle, bytes.constData(), static_cast<DWORD>(bytes.size()), &written, NULL);
}

bool takePrefixed(const QString &arg, const QString &prefix, QString *value)
{
    if (!arg.startsWith(prefix))
        return false;
    *value = arg.mid(prefix.size());
    return true;
}

bool parseGeometry(const QString &text, QRect *rect)
{
    const QStringList parts = text.split(QLatin1Char(','));
    if (parts.size() != 4)
        return false;
    bool okX = false;
    bool okY = false;
    bool okW = false;
    bool okH = false;
    const int x = parts.at(0).toInt(&okX);
    const int y = parts.at(1).toInt(&okY);
    const int w = parts.at(2).toInt(&okW);
    const int h = parts.at(3).toInt(&okH);
    if (!okX || !okY || !okW || !okH || w < 1 || h < 1)
        return false;
    *rect = QRect(x, y, w, h);
    return true;
}

class DiskCookieJar : public QNetworkCookieJar
{
public:
    explicit DiskCookieJar(const QString &path, QObject *parent = 0)
        : QNetworkCookieJar(parent), m_path(path)
    {
        load();
    }

    ~DiskCookieJar()
    {
        save();
    }

    bool setCookiesFromUrl(const QList<QNetworkCookie> &cookies, const QUrl &url)
    {
        const bool ok = QNetworkCookieJar::setCookiesFromUrl(cookies, url);
        save();
        return ok;
    }

private:
    void load()
    {
        QFile file(m_path);
        if (!file.open(QIODevice::ReadOnly))
            return;
        const QJsonDocument doc = QJsonDocument::fromJson(file.readAll());
        QList<QNetworkCookie> cookies;
        const QJsonArray list = doc.array();
        for (int i = 0; i < list.size(); ++i) {
            const QList<QNetworkCookie> parsed = QNetworkCookie::parseCookies(list.at(i).toString().toUtf8());
            cookies.append(parsed);
        }
        setAllCookies(cookies);
    }

    void save()
    {
        QJsonArray list;
        const QList<QNetworkCookie> cookies = allCookies();
        for (int i = 0; i < cookies.size(); ++i)
            list.append(QString::fromUtf8(cookies.at(i).toRawForm()));
        QFile file(m_path);
        if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate))
            return;
        file.write(QJsonDocument(list).toJson(QJsonDocument::Compact));
    }

    QString m_path;
};

class TvPage : public QWebPage
{
public:
    explicit TvPage(QObject *parent = 0)
        : QWebPage(parent)
    {
    }

    void setAgent(const QString &agent)
    {
        m_agent = agent;
    }

    void ignoreSsl(QNetworkReply *reply)
    {
        if (reply)
            reply->ignoreSslErrors();
    }

protected:
    QString userAgentForUrl(const QUrl &) const
    {
        return m_agent;
    }

    void javaScriptConsoleMessage(const QString &message, int lineNumber, const QString &sourceID)
    {
        QString where = sourceID;
        const int slash = where.lastIndexOf(QLatin1Char('/'));
        if (slash >= 0)
            where = where.mid(slash + 1);
        writeOut(where + QLatin1Char(':') + QString::number(lineNumber) + QLatin1String(": ") + message);
    }

    QWebPage *createWindow(WebWindowType)
    {
        return 0;
    }

private:
    QString m_agent;
};

class TvView : public QWebView
{
public:
    explicit TvView(QWidget *parent = 0)
        : QWebView(parent)
    {
        setContextMenuPolicy(Qt::NoContextMenu);
        setAcceptDrops(false);
        setFocusPolicy(Qt::StrongFocus);
    }

protected:
    void contextMenuEvent(QContextMenuEvent *event)
    {
        event->accept();
    }
};

struct TvGamepad
{
    WORD buttons;
    BYTE leftTrigger;
    BYTE rightTrigger;
    SHORT thumbLX;
    SHORT thumbLY;
    SHORT thumbRX;
    SHORT thumbRY;
};

struct TvXInputState
{
    DWORD packet;
    TvGamepad pad;
};

typedef DWORD (WINAPI *XInputGetStateFn)(DWORD, TvXInputState *);

struct PadBinding
{
    int mask;
    int key;
};

const PadBinding kBindings[] = {
    {0x0001, Qt::Key_Up},
    {0x0002, Qt::Key_Down},
    {0x0004, Qt::Key_Left},
    {0x0008, Qt::Key_Right},
    {0x1000, Qt::Key_Return},
    {0x2000, Qt::Key_Escape},
    {0x0100, Qt::Key_F1},
    {0x0200, Qt::Key_F2},
    {0x4000, Qt::Key_F3},
    {0x8000, Qt::Key_F4}
};

class PadPump
{
public:
    PadPump(QWidget *target, XInputGetStateFn getState)
        : m_target(target), m_getState(getState)
    {
    }

    void poll()
    {
        if (!m_getState || !m_target)
            return;
        QSet<int> want;
        for (DWORD index = 0; index < 4; ++index) {
            TvXInputState state;
            ZeroMemory(&state, sizeof(state));
            if (m_getState(index, &state) != 0)
                continue;
            const int buttons = state.pad.buttons;
            for (unsigned i = 0; i < sizeof(kBindings) / sizeof(kBindings[0]); ++i) {
                if (buttons & kBindings[i].mask)
                    want.insert(kBindings[i].key);
            }
            if (state.pad.thumbLY > 20000)
                want.insert(Qt::Key_Up);
            if (state.pad.thumbLY < -20000)
                want.insert(Qt::Key_Down);
            if (state.pad.thumbLX < -20000)
                want.insert(Qt::Key_Left);
            if (state.pad.thumbLX > 20000)
                want.insert(Qt::Key_Right);
        }

        QWidget *focus = QApplication::focusWidget();
        if (!focus)
            focus = m_target->focusProxy();
        if (!focus)
            focus = m_target;

        foreach (int key, m_down) {
            if (!want.contains(key))
                QApplication::postEvent(focus, new QKeyEvent(QEvent::KeyRelease, key, Qt::NoModifier));
        }
        foreach (int key, want) {
            if (!m_down.contains(key))
                QApplication::postEvent(focus, new QKeyEvent(QEvent::KeyPress, key, Qt::NoModifier));
        }
        m_down = want;
    }

private:
    QWidget *m_target;
    XInputGetStateFn m_getState;
    QSet<int> m_down;
};

XInputGetStateFn loadXInput()
{
    const wchar_t *names[] = {L"xinput1_4.dll", L"xinput1_3.dll", L"xinput9_1_0.dll"};
    for (unsigned i = 0; i < sizeof(names) / sizeof(names[0]); ++i) {
        HMODULE module = LoadLibraryW(names[i]);
        if (!module)
            continue;
        XInputGetStateFn fn = reinterpret_cast<XInputGetStateFn>(GetProcAddress(module, "XInputGetState"));
        if (fn)
            return fn;
        FreeLibrary(module);
    }
    return NULL;
}

void hideScrollbars(QWebPage *page)
{
    if (!page || !page->mainFrame())
        return;
    page->mainFrame()->setScrollBarPolicy(Qt::Horizontal, Qt::ScrollBarAlwaysOff);
    page->mainFrame()->setScrollBarPolicy(Qt::Vertical, Qt::ScrollBarAlwaysOff);
}

} // namespace

int main(int argc, char **argv)
{
    QApplication::setAttribute(Qt::AA_UseSoftwareOpenGL);
    QApplication app(argc, argv);
    QNetworkProxy::setApplicationProxy(QNetworkProxy(QNetworkProxy::NoProxy));

    QRect geometry(0, 0, 1280, 720);
    qreal scale = 1.0;
    QString userAgent = QLatin1String(
        "Mozilla/5.0 (SMART-TV; Linux; Tizen 2.4.0) AppleWebkit/538.1 (KHTML, like Gecko) SamsungBrowser/1.1 TV Safari/538.1");
    QString userData;
    QString url;

    const QStringList args = QCoreApplication::arguments();
    for (int i = 1; i < args.size(); ++i) {
        const QString arg = args.at(i);
        QString value;
        if (takePrefixed(arg, QLatin1String("--geometry="), &value)) {
            parseGeometry(value, &geometry);
        } else if (takePrefixed(arg, QLatin1String("--scale="), &value)) {
            bool ok = false;
            const double parsed = value.toDouble(&ok);
            if (ok && parsed >= 0.2 && parsed <= 8.0)
                scale = parsed;
        } else if (takePrefixed(arg, QLatin1String("--user-agent="), &value)) {
            if (!value.isEmpty())
                userAgent = value;
        } else if (takePrefixed(arg, QLatin1String("--user-data-dir="), &value)) {
            userData = value;
        } else if (!arg.startsWith(QLatin1String("--"))) {
            url = arg;
        }
    }

    if (url.isEmpty()) {
        writeOut(QLatin1String("missing url"));
        return 2;
    }
    if (userData.isEmpty())
        userData = QDir::tempPath() + QLatin1String("/tv-webkit");
    QDir().mkpath(userData);

    QWebSettings *settings = QWebSettings::globalSettings();
    settings->setAttribute(QWebSettings::JavascriptEnabled, true);
    settings->setAttribute(QWebSettings::LocalStorageEnabled, true);
    settings->setAttribute(QWebSettings::OfflineStorageDatabaseEnabled, true);
    settings->setAttribute(QWebSettings::OfflineWebApplicationCacheEnabled, true);
    settings->setAttribute(QWebSettings::PluginsEnabled, false);
    settings->setAttribute(QWebSettings::PrivateBrowsingEnabled, false);
    settings->setAttribute(QWebSettings::AcceleratedCompositingEnabled, false);
    settings->setAttribute(QWebSettings::WebGLEnabled, false);
    settings->setAttribute(QWebSettings::DeveloperExtrasEnabled, false);
    settings->setAttribute(QWebSettings::JavascriptCanOpenWindows, true);
    settings->setAttribute(QWebSettings::LocalContentCanAccessRemoteUrls, true);
    settings->setOfflineStorageDefaultQuota(50 * 1024 * 1024);
    QWebSettings::enablePersistentStorage(userData);

    TvView view;
    TvPage *page = new TvPage(&view);
    page->setAgent(userAgent);
    view.setPage(page);

    QNetworkAccessManager *network = page->networkAccessManager();
    network->setCookieJar(new DiskCookieJar(userData + QLatin1String("/cookies.json"), network));
    QNetworkDiskCache *cache = new QNetworkDiskCache(network);
    cache->setCacheDirectory(userData + QLatin1String("/cache"));
    network->setCache(cache);
    QObject::connect(network, &QNetworkAccessManager::sslErrors, page, [page](QNetworkReply *reply, const QList<QSslError> &) {
        page->ignoreSsl(reply);
    });

    hideScrollbars(page);
    view.setZoomFactor(scale);
    view.setWindowFlags(Qt::FramelessWindowHint | Qt::Window);
    view.setWindowTitle(QString());
    view.setGeometry(geometry);
    QObject::connect(&view, &QWebView::loadFinished, &view, [&view, page, scale](bool) {
        hideScrollbars(page);
        view.setZoomFactor(scale);
    });

    writeOut(QLatin1String("ssl ")
        + QLatin1String(QSslSocket::supportsSsl() ? "yes " : "no ")
        + QSslSocket::sslLibraryVersionString());

    view.load(QUrl(url));
    view.show();
    view.activateWindow();
    view.setFocus(Qt::OtherFocusReason);

    PadPump pump(&view, loadXInput());
    QTimer timer(&view);
    QObject::connect(&timer, &QTimer::timeout, &view, [&pump]() { pump.poll(); });
    timer.start(16);

    return app.exec();
}
