// PortProbe.h - 포트가 열려 있는지 주기적으로 확인 (MQTT 1883, MySQL 3306 표시용)
//  접속만 해 보고 바로 끊는다. 데이터는 주고받지 않는다.
#pragma once

#include <QObject>
#include <QString>
#include <QTimer>

class QTcpSocket;

class PortProbe : public QObject
{
    Q_OBJECT
public:
    PortProbe(const QString &host, quint16 port, int intervalMs, QObject *parent = nullptr);

    int state() const { return m_state; }   // -1 모름, 0 닫힘, 1 열림

signals:
    void stateChanged(bool open);

private:
    void probe();
    void setState(bool open);

    QString m_host;
    quint16 m_port;
    QTcpSocket *m_socket;
    QTimer m_timer;
    int m_state = -1;
};
