#include <QApplication>
#include <QAction>
#include <QCheckBox>
#include <QCloseEvent>
#include <QComboBox>
#include <QDateTime>
#include <QDate>
#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFileSystemWatcher>
#include <QFont>
#include <QFontMetrics>
#include <QFormLayout>
#include <QHash>
#include <QHBoxLayout>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPointer>
#include <QSet>
#include <QLabel>
#include <QLocale>
#include <QMainWindow>
#include <QMenu>
#include <QMessageBox>
#include <QPushButton>
#include <QComboBox>
#include <QSettings>
#include <QSpinBox>
#include <QStyle>
#include <QSystemTrayIcon>
#include <QTabWidget>
#include <QTabBar>
#include <QTextStream>
#include <QStringConverter>
#include <QTime>
#include <QTimer>
#include <QVector>
#include <QVBoxLayout>
#include <QWidget>
#include <QCryptographicHash>

#include <algorithm>
#include <functional>
#include <limits>
#include <optional>

namespace {

constexpr auto kApiFilenamePart = "api_port@port";
constexpr auto kDateFormat = "yyyy-MM-dd'T'HH:mm:ss";
constexpr auto kStartupRegistryPath = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr auto kStartupRegistryName = "SenkaKeisoku";

struct ApiResponse {
    QString apiPath;
    QDateTime timestamp;
    QByteArray responseBody;
    QString eventId;
};

struct ExperienceSnapshot {
    QDateTime timestamp;
    qint64 admiralExp = -1;
    qint64 shipTotalExp = -1;
    QString eventId;
};

struct Sample {
    QDateTime timestamp;
    qint64 exp = 0;
};

struct AppConfig {
    bool darkMode = false;
    bool alwaysOnTop = true;
    QString fontFamily = QStringLiteral("Yu Gothic");
    int fontSize = 12;
};

struct AppPaths {
    QString base;
    QString responses;
    QString admiralCsv;
    QString shipCsv;
    QString uiConfig;
    QString setupFlag;
    QString cleanupFlag;
};

AppPaths appPaths() {
    const QString base = QCoreApplication::applicationDirPath();
    return {base,
            base + QStringLiteral("/74en_responses"),
            base + QStringLiteral("/exp_log.csv"),
            base + QStringLiteral("/ship_exp_log.csv"),
            base + QStringLiteral("/ui_config.json"),
            base + QStringLiteral("/first_setup_done.flag"),
            base + QStringLiteral("/last_cleanup.flag")};
}

QByteArray percentDecodePreservingPlus(const QByteArray& input) {
    QByteArray decoded;
    decoded.reserve(input.size());
    auto hex = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (qsizetype i = 0; i < input.size(); ++i) {
        if (input[i] == '%' && i + 2 < input.size()) {
            const int hi = hex(input[i + 1]);
            const int lo = hex(input[i + 2]);
            if (hi >= 0 && lo >= 0) {
                decoded.append(char((hi << 4) | lo));
                i += 2;
                continue;
            }
        }
        decoded.append(input[i]);
    }
    return decoded;
}

std::optional<ExperienceSnapshot> parseApiResponse(const ApiResponse& response) {
    QByteArray body = response.responseBody;
    if (body.startsWith(QByteArray::fromHex("efbbbf"))) body.remove(0, 3);
    body = body.trimmed();
    if (body.startsWith("svdata=")) body = body.mid(7).trimmed();

    // Decode only a percent-encoded JSON object. A normal JSON body is never rewritten.
    if (body.size() >= 3 && body[0] == '%' && body[1] == '7' &&
        (body[2] == 'b' || body[2] == 'B')) {
        body = percentDecodePreservingPlus(body);
    }
    const qsizetype objectStart = body.indexOf('{');
    if (objectStart < 0) return std::nullopt;
    body = body.mid(objectStart);

    QJsonParseError error{};
    const QJsonDocument document = QJsonDocument::fromJson(body, &error);
    if (error.error != QJsonParseError::NoError || !document.isObject()) return std::nullopt;

    const QJsonObject apiData = document.object().value("api_data").toObject();
    const QJsonObject apiBasic = apiData.value("api_basic").toObject();
    const QJsonValue admiralValue = apiBasic.value("api_experience");
    ExperienceSnapshot result;
    result.timestamp = response.timestamp;
    result.eventId = response.eventId;
    if (admiralValue.isDouble()) result.admiralExp = static_cast<qint64>(admiralValue.toDouble());

    const QJsonArray ships = apiData.value("api_ship").toArray();
    if (!ships.isEmpty()) {
        qint64 total = 0;
        bool valid = true;
        for (const QJsonValue& shipValue : ships) {
            const QJsonArray exp = shipValue.toObject().value("api_exp").toArray();
            if (exp.isEmpty() || !exp[0].isDouble()) { valid = false; break; }
            const qint64 value = static_cast<qint64>(exp[0].toDouble());
            if ((value > 0 && total > std::numeric_limits<qint64>::max() - value) ||
                (value < 0 && total < std::numeric_limits<qint64>::min() - value)) {
                valid = false;
                break;
            }
            total += value;
        }
        if (valid) result.shipTotalExp = total;
    }
    if (result.admiralExp < 0 && result.shipTotalExp < 0) return std::nullopt;
    return result;
}

class CsvStorage {
public:
    static QVector<Sample> load(const QString& path, const QString& expectedHeader) {
        QVector<Sample> records;
        QFile file(path);
        if (!file.open(QIODevice::ReadOnly | QIODevice::Text)) return records;
        QTextStream stream(&file);
        stream.setEncoding(QStringConverter::Utf8);
        const QString header = stream.readLine();
        if (header != QStringLiteral("timestamp,") + expectedHeader) return records;
        while (!stream.atEnd()) {
            const QString line = stream.readLine();
            const qsizetype comma = line.indexOf(',');
            if (comma < 0) continue;
            const QDateTime timestamp = QDateTime::fromString(line.left(comma), QString::fromLatin1(kDateFormat));
            bool ok = false;
            const qint64 exp = line.mid(comma + 1).toLongLong(&ok);
            if (timestamp.isValid() && ok) records.push_back({timestamp, exp});
        }
        std::sort(records.begin(), records.end(), [](const Sample& a, const Sample& b) {
            return a.timestamp < b.timestamp;
        });
        return records;
    }

    static bool append(const QString& path, const QString& header, const Sample& sample) {
        const bool newFile = !QFileInfo::exists(path);
        QFile file(path);
        if (!file.open(QIODevice::WriteOnly | QIODevice::Append | QIODevice::Text)) return false;
        QTextStream stream(&file);
        stream.setEncoding(QStringConverter::Utf8);
        if (newFile) stream << "timestamp," << header << '\n';
        stream << sample.timestamp.toString(QString::fromLatin1(kDateFormat)) << ',' << sample.exp << '\n';
        stream.flush();
        return stream.status() == QTextStream::Ok;
    }
};

class SenkaCalculator {
public:
    static std::optional<Sample> firstInHour(const QVector<Sample>& records, const QDateTime& start) {
        const QDateTime end = start.addSecs(3600);
        const auto it = std::lower_bound(records.cbegin(), records.cend(), start,
            [](const Sample& sample, const QDateTime& time) { return sample.timestamp < time; });
        if (it != records.cend() && it->timestamp < end) return *it;
        return std::nullopt;
    }

    static std::optional<double> rate(const std::optional<Sample>& a,
                                      const std::optional<Sample>& b,
                                      bool senka) {
        if (!a || !b || b->timestamp <= a->timestamp || b->exp < a->exp) return std::nullopt;
        const qint64 elapsed = a->timestamp.secsTo(b->timestamp);
        if (elapsed <= 0) return std::nullopt;
        const double perHour = double(b->exp - a->exp) * 3600.0 / double(elapsed);
        return senka ? perHour * 7.0 / 10000.0 : perHour;
    }

    static QString display(const QVector<Sample>& records, const QDateTime& now, bool ship) {
        if (records.isEmpty()) return QStringLiteral("データなし");
        QDateTime currentHour = now;
        currentHour.setTime(QTime(now.time().hour(), 0));
        const QDateTime previousHour = currentHour.addSecs(-3600);
        const QDateTime twoHoursAgo = currentHour.addSecs(-7200);
        const auto r1 = firstInHour(records, twoHoursAgo);
        const auto r2 = firstInHour(records, previousHour);
        const auto r3 = firstInHour(records, currentHour);
        const std::optional<Sample> latest = records.back();
        auto text = [ship](const std::optional<double>& value) {
            if (!value) return QStringLiteral("記録不足");
            if (ship) return QLocale(QLocale::English, QLocale::UnitedStates).toString(qRound64(*value));
            return QString::number(*value, 'f', 2);
        };
        const auto line = [&](const QDateTime& from, const QDateTime& to,
                              const std::optional<Sample>& a, const std::optional<Sample>& b) {
            const QString label = ship ? QStringLiteral(" Exp時速 ") : QStringLiteral(" 戦果時速 ");
            return QStringLiteral("・%1~%2%3%4")
                .arg(from.toString("HH:mm"), to.toString("HH:mm"), label,
                     text(rate(a, b, !ship)));
        };
        return line(twoHoursAgo, previousHour, r1, r2) + '\n' +
               line(previousHour, currentHour, r2, r3) + '\n' +
               line(currentHour, now, r3, latest);
    }
};

class ICaptureSource {
public:
    using ResponseHandler = std::function<void(const ApiResponse&, const ExperienceSnapshot&)>;
    virtual ~ICaptureSource() = default;
    virtual bool start() = 0;
    virtual void stop() = 0;
    virtual bool isRunning() const = 0;
};

class ApiFileCapture final : public QObject, public ICaptureSource {
public:
    using ResponseHandler = ICaptureSource::ResponseHandler;

    ApiFileCapture(QString directory, ResponseHandler handler, QObject* parent = nullptr)
        : QObject(parent), m_directory(std::move(directory)), m_handler(std::move(handler)) {
        connect(&m_watcher, &QFileSystemWatcher::directoryChanged, this,
                [this](const QString&) { scan(true); });
        connect(&m_watcher, &QFileSystemWatcher::fileChanged, this,
                [this](const QString& path) { enqueueIfChanged(path); });
        connect(&m_pollTimer, &QTimer::timeout, this, [this] { processPending(); });
        m_pollTimer.setInterval(500);
    }

    bool start() override {
        if (!QDir().mkpath(m_directory)) return false;
        scan(false); // Seed signatures so old files are not imported on startup.
        if (!m_watcher.directories().contains(m_directory) && !m_watcher.addPath(m_directory)) return false;
        m_pollTimer.start();
        return true;
    }
    void stop() override { m_pollTimer.stop(); m_watcher.removePaths(m_watcher.files()); m_watcher.removePaths(m_watcher.directories()); }
    bool isRunning() const override { return m_pollTimer.isActive(); }

private:
    struct Pending { QString path; int attempts = 0; };
    QString m_directory;
    ResponseHandler m_handler;
    QFileSystemWatcher m_watcher;
    QTimer m_pollTimer;
    QHash<QString, QByteArray> m_seenSignature;
    QHash<QString, int> m_pending;
    QSet<QString> m_eventIds;

    static bool isTarget(const QFileInfo& info) {
        return info.isFile() && info.fileName().contains(QString::fromLatin1(kApiFilenamePart), Qt::CaseInsensitive) &&
               info.suffix().compare(QStringLiteral("json"), Qt::CaseInsensitive) == 0;
    }
    static QByteArray signature(const QFileInfo& info) {
        return QByteArray::number(info.size()) + ':' + QByteArray::number(info.lastModified().toMSecsSinceEpoch());
    }
    void watchFile(const QString& path) {
        if (QFileInfo::exists(path) && !m_watcher.files().contains(path)) m_watcher.addPath(path);
    }
    void scan(bool enqueueNewOrChanged) {
        QDir dir(m_directory);
        const QFileInfoList files = dir.entryInfoList(QDir::Files | QDir::NoDotAndDotDot, QDir::Name);
        for (const QFileInfo& info : files) {
            if (!isTarget(info)) continue;
            const QString path = info.absoluteFilePath();
            watchFile(path);
            const QByteArray sig = signature(info);
            if (!m_seenSignature.contains(path)) {
                if (enqueueNewOrChanged) m_pending.insert(path, 0);
                else m_seenSignature.insert(path, sig);
            } else if (enqueueNewOrChanged && m_seenSignature.value(path) != sig) {
                m_pending.insert(path, 0);
            }
        }
        if (!m_watcher.directories().contains(m_directory)) m_watcher.addPath(m_directory);
    }
    void enqueueIfChanged(const QString& path) {
        const QFileInfo info(path);
        if (!isTarget(info)) return;
        watchFile(path);
        const QByteArray sig = signature(info);
        if (!m_seenSignature.contains(path) || m_seenSignature.value(path) != sig)
            m_pending.insert(path, 0);
    }
    void processPending() {
        const QStringList paths = m_pending.keys();
        for (const QString& path : paths) {
            const QFileInfo before(path);
            if (!before.exists()) { m_pending.remove(path); continue; }
            const QByteArray sig = signature(before);
            if (m_seenSignature.value(path) == sig) { m_pending.remove(path); continue; }

            QFile file(path);
            if (!file.open(QIODevice::ReadOnly)) { retry(path); continue; }
            const QByteArray body = file.readAll();
            const QFileInfo after(path);
            if (!after.exists() || signature(after) != sig) { retry(path); continue; }
            const QDateTime timestamp = after.lastModified();
            const QString eventId = QString::fromLatin1(QCryptographicHash::hash(
                timestamp.toString(Qt::ISODateWithMs).toUtf8() + '\n' + body, QCryptographicHash::Sha256).toHex());
            const ApiResponse response{QStringLiteral("/kcsapi/api_port/port"), timestamp, body, eventId};
            const auto parsed = parseApiResponse(response);
            if (!parsed) { retry(path); continue; }
            if (!m_eventIds.contains(eventId)) {
                m_eventIds.insert(eventId);
                if (m_eventIds.size() > 4096) m_eventIds.erase(m_eventIds.begin());
                m_handler(response, *parsed);
            }
            m_seenSignature.insert(path, sig);
            m_pending.remove(path);
            watchFile(path);
        }
    }
    void retry(const QString& path) {
        int attempts = m_pending.value(path, 0) + 1;
        if (attempts >= 5) m_pending.remove(path);
        else m_pending.insert(path, attempts);
    }
};

class SettingsDialog final : public QDialog {
public:
    SettingsDialog(const AppConfig& config, QWidget* parent = nullptr) : QDialog(parent) {
        setWindowTitle(QStringLiteral("設定"));
        setWindowFlag(Qt::WindowStaysOnTopHint, true);
        setModal(false);
        auto* root = new QVBoxLayout(this);
        auto* appearance = new QFormLayout;
        m_dark = new QCheckBox(QStringLiteral("有効"), this);
        m_dark->setChecked(config.darkMode);
        m_font = new QComboBox(this);
        m_font->addItems({QStringLiteral("Yu Gothic"), QStringLiteral("メイリオ"),
                          QStringLiteral("MS Gothic"), QStringLiteral("Arial"),
                          QStringLiteral("Courier New")});
        const int fontIndex = m_font->findText(config.fontFamily);
        m_font->setCurrentIndex(fontIndex >= 0 ? fontIndex : 0);
        m_fontSize = new QSpinBox(this);
        m_fontSize->setRange(8, 24);
        m_fontSize->setValue(config.fontSize);
        m_topmost = new QCheckBox(QStringLiteral("有効"), this);
        m_topmost->setChecked(config.alwaysOnTop);
        m_startup = new QCheckBox(QStringLiteral("有効"), this);
        m_startup->setChecked(startupEnabled());
        appearance->addRow(QStringLiteral("【外観】"), new QLabel);
        appearance->addRow(QStringLiteral("ダークモード"), m_dark);
        appearance->addRow(QStringLiteral("フォント"), m_font);
        appearance->addRow(QStringLiteral("フォントサイズ"), m_fontSize);
        appearance->addRow(QStringLiteral("プレビュー"), new QLabel(QStringLiteral("戦果時速 12.34"), this));
        appearance->addRow(QStringLiteral("【表示】"), new QLabel);
        appearance->addRow(QStringLiteral("常に最前面に表示\n(74enへの取り込み時はOFF)"), m_topmost);
        appearance->addRow(QStringLiteral("【起動】"), new QLabel);
        appearance->addRow(QStringLiteral("Windowsログオン時に自動起動"), m_startup);
        root->addLayout(appearance);
        auto* buttons = new QHBoxLayout;
        auto* apply = new QPushButton(QStringLiteral("適用"), this);
        auto* close = new QPushButton(QStringLiteral("閉じる"), this);
        m_preview = qobject_cast<QLabel*>(appearance->itemAt(4, QFormLayout::FieldRole)->widget());
        buttons->addStretch(); buttons->addWidget(apply); buttons->addWidget(close);
        root->addLayout(buttons);
        connect(close, &QPushButton::clicked, this, &QDialog::close);
        connect(apply, &QPushButton::clicked, this, [this] {
            emitApply(m_dark->isChecked(), m_font->currentText(), m_fontSize->value(),
                      m_topmost->isChecked(), m_startup->isChecked());
        });
        const auto updatePreview = [this] {
            QFont preview(m_font->currentText(), m_fontSize->value()); preview.setBold(true);
            m_preview->setFont(preview);
        };
        connect(m_font, &QComboBox::currentTextChanged, this, [updatePreview] { updatePreview(); });
        connect(m_fontSize, &QSpinBox::valueChanged, this, [updatePreview](int) { updatePreview(); });
        updatePreview();
        resize(430, 360);
        setAttribute(Qt::WA_DeleteOnClose, true);
    }

    std::function<void(bool, const QString&, int, bool, bool)> onApply;
private:
    QCheckBox* m_dark{};
    QComboBox* m_font{};
    QSpinBox* m_fontSize{};
    QCheckBox* m_topmost{};
    QCheckBox* m_startup{};
    QLabel* m_preview{};
    void emitApply(bool dark, const QString& family, int size, bool topmost, bool startup) {
        if (onApply) onApply(dark, family, size, topmost, startup);
    }
    static bool startupEnabled() {
        QSettings run(QString::fromLatin1(kStartupRegistryPath), QSettings::NativeFormat);
        return run.contains(QString::fromLatin1(kStartupRegistryName));
    }
};

class MainWindow final : public QMainWindow {
public:
    explicit MainWindow(AppPaths paths) : m_paths(std::move(paths)) {
        setWindowTitle(QStringLiteral("戦果時速計測機"));
        loadConfig();
        setWindowFlag(Qt::WindowStaysOnTopHint, m_config.alwaysOnTop);
        m_admiral = CsvStorage::load(m_paths.admiralCsv, QStringLiteral("admiral_exp"));
        m_ships = CsvStorage::load(m_paths.shipCsv, QStringLiteral("ship_total_exp"));

        auto* central = new QWidget(this);
        auto* layout = new QVBoxLayout(central);
        layout->setContentsMargins(8, 8, 8, 4);
        m_tabs = new QTabWidget(central);
        m_admiralLabel = makeLabel();
        m_shipLabel = makeLabel();
        m_tabs->addTab(m_admiralLabel, QStringLiteral("戦果時速"));
        m_tabs->addTab(m_shipLabel, QStringLiteral("艦娘Exp時速"));
        layout->addWidget(m_tabs, 1);
        auto* bottom = new QHBoxLayout;
        bottom->addStretch();
        auto* settingsButton = new QPushButton(QStringLiteral("⚙ 設定"), central);
        settingsButton->setMaximumWidth(84);
        bottom->addWidget(settingsButton);
        layout->addLayout(bottom);
        setCentralWidget(central);
        connect(settingsButton, &QPushButton::clicked, this, [this] { openSettings(); });
        connect(m_tabs, &QTabWidget::currentChanged, this, [this] { updateDisplay(); });

        m_tray = new QSystemTrayIcon(style()->standardIcon(QStyle::SP_ComputerIcon), this);
        m_tray->setToolTip(QStringLiteral("戦果時速計測機"));
        auto* trayMenu = new QMenu(this);
        QAction* showAction = trayMenu->addAction(QStringLiteral("ウィンドウを開く"));
        QAction* settingsAction = trayMenu->addAction(QStringLiteral("設定"));
        trayMenu->addSeparator();
        QAction* quitAction = trayMenu->addAction(QStringLiteral("終了"));
        connect(showAction, &QAction::triggered, this, [this] { showNormal(); raise(); activateWindow(); });
        connect(settingsAction, &QAction::triggered, this, [this] { openSettings(); });
        connect(quitAction, &QAction::triggered, qApp, &QApplication::quit);
        m_tray->setContextMenu(trayMenu);
        connect(m_tray, &QSystemTrayIcon::activated, this, [this](QSystemTrayIcon::ActivationReason reason) {
            if (reason == QSystemTrayIcon::DoubleClick) { showNormal(); raise(); activateWindow(); }
        });
        m_tray->show();

        auto* fileCapture = new ApiFileCapture(m_paths.responses, [this](const ApiResponse& response, const ExperienceSnapshot& snapshot) {
            processResponse(response, snapshot);
        }, this);
        m_capture = fileCapture;
        if (!m_capture->start())
            QMessageBox::warning(this, QStringLiteral("監視エラー"), QStringLiteral("74en_responsesを監視できませんでした。"));

        m_refreshTimer = new QTimer(this);
        m_refreshTimer->setInterval(500);
        connect(m_refreshTimer, &QTimer::timeout, this, [this] { updateDisplay(); });
        m_refreshTimer->start();
        m_cleanupTimer = new QTimer(this);
        m_cleanupTimer->setInterval(60 * 1000);
        connect(m_cleanupTimer, &QTimer::timeout, this, [this] { cleanupOldJsonHourly(); });
        m_cleanupTimer->start();
        cleanupOldJsonHourly();
        updateDisplay();
        showFirstRunGuide();
        applyTheme();
        resizeForFont();
    }

protected:
    void closeEvent(QCloseEvent* event) override {
        event->ignore();
        hide();
    }

private:
    AppPaths m_paths;
    AppConfig m_config;
    QTabWidget* m_tabs{};
    QLabel* m_admiralLabel{};
    QLabel* m_shipLabel{};
    QSystemTrayIcon* m_tray{};
    ICaptureSource* m_capture{};
    QTimer* m_refreshTimer{};
    QTimer* m_cleanupTimer{};
    QVector<Sample> m_admiral;
    QVector<Sample> m_ships;
    QPointer<SettingsDialog> m_settings;

    QLabel* makeLabel() {
        auto* label = new QLabel;
        label->setAlignment(Qt::AlignLeft | Qt::AlignTop);
        label->setMargin(6);
        label->setWordWrap(false);
        QFont font = this->font();
        font.setBold(true);
        font.setPointSize(m_config.fontSize + 2);
        label->setFont(font);
        return label;
    }
    void processResponse(const ApiResponse&, const ExperienceSnapshot& snapshot) {
        bool changed = false;
        if (snapshot.admiralExp >= 0) {
            Sample sample{snapshot.timestamp, snapshot.admiralExp};
            if (CsvStorage::append(m_paths.admiralCsv, QStringLiteral("admiral_exp"), sample)) {
                m_admiral.push_back(sample); changed = true;
            }
        }
        if (snapshot.shipTotalExp >= 0) {
            Sample sample{snapshot.timestamp, snapshot.shipTotalExp};
            if (CsvStorage::append(m_paths.shipCsv, QStringLiteral("ship_total_exp"), sample)) {
                m_ships.push_back(sample); changed = true;
            }
        }
        if (changed) {
            auto sortSamples = [](QVector<Sample>& samples) {
                std::sort(samples.begin(), samples.end(), [](const Sample& a, const Sample& b) {
                    return a.timestamp < b.timestamp;
                });
            };
            sortSamples(m_admiral); sortSamples(m_ships); updateDisplay();
        }
    }
    void updateDisplay() {
        const QDateTime now = QDateTime::currentDateTime();
        m_admiralLabel->setText(SenkaCalculator::display(m_admiral, now, false));
        m_shipLabel->setText(SenkaCalculator::display(m_ships, now, true));
    }
    void openSettings() {
        if (m_settings) { m_settings->show(); m_settings->raise(); m_settings->activateWindow(); return; }
        auto* dialog = new SettingsDialog(m_config, this);
        m_settings = dialog;
        connect(dialog, &QObject::destroyed, this, [this] { m_settings = nullptr; });
        dialog->onApply = [this](bool dark, const QString& family, int size, bool topmost, bool startup) {
            m_config.darkMode = dark; m_config.fontFamily = family; m_config.fontSize = size;
            m_config.alwaysOnTop = topmost;
            saveConfig(); setStartup(startup);
            setWindowFlag(Qt::WindowStaysOnTopHint, topmost);
            applyTheme(); updateDisplay(); resizeForFont();
            show();
        };
        dialog->show();
    }
    void loadConfig() {
        QFile file(m_paths.uiConfig);
        if (!file.open(QIODevice::ReadOnly)) return;
        const QJsonObject o = QJsonDocument::fromJson(file.readAll()).object();
        m_config.darkMode = o.value("dark_mode").toBool(m_config.darkMode);
        m_config.fontFamily = o.value("font_family").toString(m_config.fontFamily);
        m_config.fontSize = qBound(8, o.value("font_size").toInt(m_config.fontSize), 24);
        m_config.alwaysOnTop = o.value("always_on_top").toBool(m_config.alwaysOnTop);
    }
    void saveConfig() const {
        QJsonObject o;
        o.insert("dark_mode", m_config.darkMode);
        o.insert("font_family", m_config.fontFamily);
        o.insert("font_size", m_config.fontSize);
        o.insert("always_on_top", m_config.alwaysOnTop);
        QFile file(m_paths.uiConfig);
        if (file.open(QIODevice::WriteOnly | QIODevice::Truncate))
            file.write(QJsonDocument(o).toJson(QJsonDocument::Indented));
    }
    void setStartup(bool enabled) const {
        QSettings run(QString::fromLatin1(kStartupRegistryPath), QSettings::NativeFormat);
        const QString key = QString::fromLatin1(kStartupRegistryName);
        if (enabled) run.setValue(key, QLatin1Char('"') + QCoreApplication::applicationFilePath() + QLatin1Char('"'));
        else run.remove(key);
    }
    void applyTheme() {
        const QString bg = m_config.darkMode ? QStringLiteral("#202124") : QStringLiteral("#f0f0f0");
        const QString fg = m_config.darkMode ? QStringLiteral("#E8EAED") : QStringLiteral("#000000");
        const QString entry = m_config.darkMode ? QStringLiteral("#2d2d2d") : QStringLiteral("#ffffff");
        qApp->setStyleSheet(QStringLiteral(
            "QWidget { background-color: %1; color: %2; }"
            "QLineEdit, QComboBox, QSpinBox { background-color: %3; }"
            "QTabBar::tab { padding: 4px 8px; }"
            "QTabBar::tab:selected { background-color: %4; color: white; }")
            .arg(bg, fg, entry, m_config.darkMode ? QStringLiteral("#264f78") : QStringLiteral("#0078d7")));
        QFont font(m_config.fontFamily, m_config.fontSize);
        qApp->setFont(font);
        QFont labelFont = font; labelFont.setBold(true); labelFont.setPointSize(m_config.fontSize + 2);
        m_admiralLabel->setFont(labelFont); m_shipLabel->setFont(labelFont);
    }
    void resizeForFont() {
        QFont displayFont(m_config.fontFamily, m_config.fontSize + 2);
        displayFont.setBold(true);
        const QFontMetrics metrics(displayFont);
        const int textWidth = qMax(
            metrics.horizontalAdvance(QStringLiteral("・00:00~00:00 Exp時速 999,999")),
            metrics.horizontalAdvance(QStringLiteral("・00:00~00:00 戦果時速 9999.99")));
        const int labelWidth = textWidth + 12; // QLabel's left/right margins
        const int labelHeight = metrics.lineSpacing() * 3 + 12;
        const QSize labelSize(labelWidth, labelHeight);
        m_admiralLabel->setMinimumSize(labelSize);
        m_shipLabel->setMinimumSize(labelSize);
        m_tabs->setMinimumSize(labelWidth + 16,
                               labelHeight + m_tabs->tabBar()->sizeHint().height() + 12);
        setMinimumSize(0, 0);
        setMaximumSize(QWIDGETSIZE_MAX, QWIDGETSIZE_MAX);
        centralWidget()->adjustSize();
        adjustSize();
        setFixedSize(sizeHint().expandedTo(minimumSizeHint()));
    }
    void showFirstRunGuide() {
        if (QFileInfo::exists(m_paths.setupFlag)) return;
        const QString text = QStringLiteral(
            "以下の設定を行ってください：\n\n"
            "1. 74en を起動\n"
            "2. ファイル → 設定 → 通信 タブ\n"
            "3. 『通信内容を保存する』にチェック\n"
            "4. 『response』にチェック\n"
            "5. 保存先フォルダとして以下を指定\n\n%1").arg(m_paths.responses);
        QMessageBox::information(this, QStringLiteral("初期設定"), text);
        QFile flag(m_paths.setupFlag); if (flag.open(QIODevice::WriteOnly)) flag.write("ok");
    }
    void cleanupOldJsonHourly() {
        QFile flag(m_paths.cleanupFlag);
        const QDateTime now = QDateTime::currentDateTime();
        if (!flag.exists()) {
            if (flag.open(QIODevice::WriteOnly)) flag.write(now.toString(QString::fromLatin1(kDateFormat)).toUtf8());
            return;
        }
        QDateTime last;
        if (flag.open(QIODevice::ReadOnly)) {
            last = QDateTime::fromString(QString::fromUtf8(flag.readAll()).trimmed(), QString::fromLatin1(kDateFormat));
            flag.close();
        }
        if (last.isValid() && last.secsTo(now) < 3600) return;
        QDir dir(m_paths.responses);
        const QFileInfoList files = dir.entryInfoList({QStringLiteral("*.json")}, QDir::Files);
        for (const QFileInfo& info : files)
            if (info.lastModified().secsTo(now) > 48 * 3600) QFile::remove(info.absoluteFilePath());
        if (flag.open(QIODevice::WriteOnly | QIODevice::Truncate))
            flag.write(now.toString(QString::fromLatin1(kDateFormat)).toUtf8());
    }
};

} // namespace

int main(int argc, char* argv[]) {
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("SenkaSpeedMeasuringDevice"));
    QApplication::setOrganizationName(QStringLiteral("SenkaSpeedMeasuringDevice"));
    QApplication::setQuitOnLastWindowClosed(false);
    const AppPaths paths = appPaths();
    QDir().mkpath(paths.responses);
    MainWindow window(paths);
    window.show();
    return app.exec();
}

