// MqttLite.h - 받기 전용 간단한 MQTT 3.1.1 클라이언트 (QTcpSocket으로 직접 구현)
//  Qt 무료 버전에는 MQTT 모듈이 없어서, 서버 UI에 필요한 만큼만 만든다.
//  하는 일: 연결(CONNECT) → 구독(SUBSCRIBE) → 메시지 받기(PUBLISH, QoS 1이면 PUBACK) → 연결 유지(PINGREQ)
//  보내기(PUBLISH)는 하지 않는다. 연결이 끊기면 kReconnectMs 뒤 다시 연결한다.
//
//  MQTT 패킷 모양 (모든 패킷 공통)
//    [첫 바이트: 종류(위 4비트) + 플래그(아래 4비트)] [남은 길이: 1~4바이트 가변 길이] [나머지]
#pragma once

#include <QByteArray>
#include <QDateTime>
#include <QObject>
#include <QStringList>

class QTcpSocket;
class QTimer;

struct MqttMessage
{
    QDateTime received;   // 이 PC가 받은 시각
    QString topic;
    QByteArray payload;
    int qos = 0;
    bool retained = false;   // 브로커가 보관하던 메시지 (구독 직후 한 번 옴)
};

class MqttLite : public QObject
{
    Q_OBJECT
public:
    MqttLite(const QString &host, quint16 port, const QString &clientId, const QStringList &topics,
             QObject *parent = nullptr);

    void start();
    bool isConnected() const { return m_connected; }

signals:
    void connectedChanged(bool connected, const QString &reason);
    void messageReceived(const MqttMessage &msg);

private:
    void openSocket();
    void onSocketConnected();
    void onSocketClosed(const QString &reason);
    void onReadyRead();
    bool takePacket(quint8 &header, QByteArray &body);   // 버퍼에서 패킷 하나를 꺼냄 (덜 왔으면 false)
    void handlePacket(quint8 header, const QByteArray &body);
    void send(quint8 header, const QByteArray &body);

    static QByteArray encodeLength(int length);
    static QByteArray encodeString(const QString &text);

    QString m_host;
    quint16 m_port;
    QString m_clientId;
    QStringList m_topics;

    QTcpSocket *m_socket;
    QTimer *m_pingTimer;
    QTimer *m_reconnectTimer;
    QByteArray m_buffer;
    bool m_connected = false;
    quint16 m_nextPacketId = 1;
};
