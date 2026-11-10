// 디팔 서버 관리 창 (서버 UI)
//  서버 PC에서 브로커·중앙 서버·컨트롤러·DB 저장 프로그램을 켜고 끄고, 상태와 로그를 본다.
//  작업자용 화면(라인 현황·결과·통계)은 관리자 프로그램(WPF)이 맡는다.
//  모양: Windows 관리 도구식 어두운 테마 (Visual Studio 다크와 비슷한 색). 색은 상태에만 쓴다.
#include "MainWindow.h"

#include <QApplication>
#include <QFont>
#include <QPalette>
#include <QStyleFactory>

namespace {

// 색 (수정할 값)
const QColor kWindow(0x2D, 0x2D, 0x30);     // 창 바탕
const QColor kBase(0x1E, 0x1E, 0x1E);       // 표·로그 바탕
const QColor kAltBase(0x25, 0x25, 0x26);
const QColor kText(0xF1, 0xF1, 0xF1);       // 글자 (밝게)
const QColor kDisabled(0x7A, 0x7A, 0x7A);
const QColor kButton(0x33, 0x33, 0x37);
const QColor kHighlight(0x26, 0x4F, 0x78);  // 고른 줄
const QColor kLink(0x4F, 0xA3, 0xE3);

QPalette darkPalette()
{
    QPalette p;
    p.setColor(QPalette::Window, kWindow);
    p.setColor(QPalette::WindowText, kText);
    p.setColor(QPalette::Base, kBase);
    p.setColor(QPalette::AlternateBase, kAltBase);
    p.setColor(QPalette::Text, kText);
    p.setColor(QPalette::Button, kButton);
    p.setColor(QPalette::ButtonText, kText);
    p.setColor(QPalette::BrightText, Qt::white);
    p.setColor(QPalette::Highlight, kHighlight);
    p.setColor(QPalette::HighlightedText, Qt::white);
    p.setColor(QPalette::Link, kLink);
    p.setColor(QPalette::ToolTipBase, QColor(0x42, 0x42, 0x45));
    p.setColor(QPalette::ToolTipText, kText);
    p.setColor(QPalette::PlaceholderText, kDisabled);
    p.setColor(QPalette::Mid, QColor(0x3F, 0x3F, 0x46));
    p.setColor(QPalette::Dark, QColor(0x1B, 0x1B, 0x1C));
    for (auto role : {QPalette::Text, QPalette::WindowText, QPalette::ButtonText})
        p.setColor(QPalette::Disabled, role, kDisabled);
    p.setColor(QPalette::Inactive, QPalette::Highlight, kHighlight);   // 창이 비활성이어도 고른 줄이 보이게
    return p;
}

// 팔레트로 안 되는 부분만 (표 머리글, 경계선, 상태 줄)
const char *kStyleSheet = R"(
QToolBar { background: #2D2D30; border: none; border-bottom: 1px solid #3F3F46; spacing: 2px; padding: 2px; }
QToolButton { padding: 3px 6px; border: 1px solid transparent; }
QToolButton:hover { background: #3E3E40; border-color: #3E3E40; }
QToolButton:pressed { background: #007ACC; }
QTableWidget { border: none; gridline-color: #333337; }
QTableWidget::item { padding-left: 4px; border-bottom: 1px solid #2A2A2D; }
QHeaderView::section { background: #252526; color: #D0D0D0; border: none; border-right: 1px solid #3F3F46;
                       border-bottom: 1px solid #3F3F46; padding: 3px 6px; }
QSplitter::handle { background: #3F3F46; height: 4px; }
QPlainTextEdit { border: none; }
#detailInfo { background: #252526; }
#detailInfo #key { color: #A0A0A0; }
#divider { color: #3F3F46; }
QStatusBar { background: #007ACC; color: #FFFFFF; }
QStatusBar QLabel { color: #FFFFFF; padding: 0 8px; border-right: 1px solid rgba(255,255,255,70); }
QStatusBar::item { border: none; }
)";

}  // namespace

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("DEPAL 서버"));
    QApplication::setStyle(QStyleFactory::create(QStringLiteral("Fusion")));
    QApplication::setPalette(darkPalette());
    app.setFont(QFont(QStringLiteral("Malgun Gothic"), 9));
    app.setStyleSheet(QString::fromUtf8(kStyleSheet));

    MainWindow window;
    window.show();
    return app.exec();
}
