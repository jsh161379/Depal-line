#include "MqttLite.h"

#include <QTcpSocket>
#include <QTimer>

namespace {

// 수정할 값
constexpr quint16 kKeepAliveSec = 30;   // 이 시간 안에 아무것도 안 보내면 브로커가 끊음 → 절반마다 PINGREQ
constexpr int kReconnectMs = 3000;      // 끊긴 뒤 다시 연결까지
constexpr int kMaxPacket = 1024 * 1024; // 이보다 큰 패킷은 이상한 것으로 보고 연결을 끊음

// 패킷 종류 (첫 바이트 위 4비트)
constexpr quint8 CONNECT = 0x10, CONNACK = 0x20, PUBLISH = 0x30, PUBACK = 0x40,
                 SUBSCRIBE = 0x82,   // SUBSCRIBE는 플래그 0010이 규칙
                 SUBACK = 0x90, PINGREQ = 0xC0, PINGRESP = 0xD0;

}  // namespace

MqttLite::MqttLite(const QString &host, quint16 port, const QString &clientId, const QStringList &topics,
                   QObject *parent)
    : QObject(parent), m_host(host), m_port(port), m_clientId(clientId), m_topics(topics),
      m_socket(new QTcpSocket(this)), m_pingTimer(new QTimer(this)), m_reconnectTimer(new QTimer(this))
{
    m_pingTimer->setInterval(kKeepAliveSec * 1000 / 2);
    m_reconnectTimer->setSingleShot(true);
    m_reconnectTimer->setInterval(kReconnectMs);

    connect(m_socket, &QTcpSocket::connected, this, &MqttLite::onSocketConnected);
    connect(m_socket, &QTcpSocket::readyRead, this, &MqttLite::onReadyRead);
    connect(m_socket, &QTcpSocket::disconnected, this, [this] { onSocketClosed(QStringLiteral("브로커가 연결을 끊음")); });
    connect(m_socket, &QTcpSocket::errorOccurred, this, [this](QAbstractSocket::SocketError) {
        onSocketClosed(m_socket->errorString());
    });
    connect(m_pingTimer, &QTimer::timeout, this, [this] { send(PINGREQ, {}); });
    connect(m_reconnectTimer, &QTimer::timeout, this, &MqttLite::openSocket);
}

void MqttLite::start()
{
    openSocket();
}

void MqttLite::openSocket()
{
    m_buffer.clear();
    m_socket->abort();
    m_socket->connectToHost(m_host, m_port);
}

void MqttLite::onSocketConnected()
{
    // CONNECT: 프로토콜 이름 "MQTT", 레벨 4(=3.1.1), 플래그 0x02(clean session), keep alive, 클라이언트 ID
    QByteArray body;
    body += encodeString(QStringLiteral("MQTT"));
    body += char(4);
    body += char(0x02);
    body += char(kKeepAliveSec >> 8);
    body += char(kKeepAliveSec & 0xFF);
    body += encodeString(m_clientId);
    send(CONNECT, body);
}

void MqttLite::onSocketClosed(const QString &reason)
{
    m_pingTimer->stop();
    if (m_connected) {
        m_connected = false;
        emit connectedChanged(false, reason);
    }
    if (!m_reconnectTimer->isActive())
        m_reconnectTimer->start();
}

void MqttLite::onReadyRead()
{
    m_buffer += m_socket->readAll();
    quint8 header = 0;
    QByteArray body;
    while (takePacket(header, body))
        handlePacket(header, body);
}

bool MqttLite::takePacket(quint8 &header, QByteArray &body)
{
    if (m_buffer.size() < 2)
        return false;
    // 남은 길이: 한 바이트에 7비트씩, 맨 위 비트가 1이면 다음 바이트가 이어짐 (최대 4바이트)
    int length = 0, multiplier = 1, pos = 1;
    while (true) {
        if (pos >= m_buffer.size())
            return false;                       // 길이 바이트가 아직 다 안 옴
        const quint8 b = quint8(m_buffer[pos++]);
        length += (b & 0x7F) * multiplier;
        if (!(b & 0x80))
            break;
        multiplier *= 128;
        if (pos > 4) {                          // 규칙 위반
            onSocketClosed(QStringLiteral("잘못된 패킷 길이"));
            m_socket->abort();
            return false;
        }
    }
    if (length > kMaxPacket) {
        m_socket->abort();
        onSocketClosed(QStringLiteral("패킷이 너무 큼"));
        return false;
    }
    if (m_buffer.size() < pos + length)
        return false;                           // 나머지가 아직 다 안 옴
    header = quint8(m_buffer[0]);
    body = m_buffer.mid(pos, length);
    m_buffer.remove(0, pos + length);
    return true;
}

void MqttLite::handlePacket(quint8 header, const QByteArray &body)
{
    switch (header & 0xF0) {
    case CONNACK: {
        const int code = body.size() >= 2 ? quint8(body[1]) : -1;
        if (code != 0) {
            m_socket->abort();
            onSocketClosed(QStringLiteral("브로커가 연결을 거부함 (코드 %1)").arg(code));
            return;
        }
        sendSubscribe();
        m_pingTimer->start();
        m_connected = true;
        emit connectedChanged(true, QString());
        break;
    }
    case PUBLISH: {
        // 플래그: bit3 DUP, bit1~2 QoS, bit0 RETAIN
        const int qos = (header >> 1) & 0x03;
        const bool retained = header & 0x01;
        if (body.size() < 2)
            return;
        const int topicLen = (quint8(body[0]) << 8) | quint8(body[1]);
        int pos = 2 + topicLen;
        if (body.size() < pos)
            return;
        MqttMessage msg;
        msg.received = QDateTime::currentDateTime();
        msg.topic = QString::fromUtf8(body.mid(2, topicLen));
        msg.qos = qos;
        msg.retained = retained;
        if (qos > 0) {                          // QoS 1·2는 패킷 ID가 붙음
            if (body.size() < pos + 2)
                return;
            const QByteArray packetId = body.mid(pos, 2);
            pos += 2;
            if (qos == 1)
                send(PUBACK, packetId);         // 받았다고 알림 (안 보내면 브로커가 다시 보냄)
        }
        msg.payload = body.mid(pos);
        emit messageReceived(msg);
        break;
    }
    case PUBACK:
        if (body.size() >= 2)
            emit published(quint16((quint8(body[0]) << 8) | quint8(body[1])));
        break;
    case SUBACK:
    case PINGRESP:
        break;
    default:
        break;
    }
}

void MqttLite::sendSubscribe()
{
    // 구독: 패킷 ID + (토픽, 원하는 QoS) 목록. QoS 1로 받으면 브로커가 보낸 QoS가 그대로 보임
    QByteArray sub;
    const quint16 id = nextPacketId();
    sub += char(id >> 8);
    sub += char(id & 0xFF);
    for (const auto &t : m_topics) {
        sub += encodeString(t);
        sub += char(1);
    }
    send(SUBSCRIBE, sub);
}

void MqttLite::resubscribe()
{
    if (m_connected)
        sendSubscribe();
}

quint16 MqttLite::publish(const QString &topic, const QByteArray &payload, int qos, bool retain)
{
    if (!m_connected)
        return 0;
    // 첫 바이트: PUBLISH(0x30) + QoS(bit1~2) + RETAIN(bit0)
    const quint8 header = PUBLISH | quint8((qos & 0x03) << 1) | (retain ? 0x01 : 0x00);
    QByteArray body = encodeString(topic);
    quint16 id = 0;
    if (qos > 0) {
        id = nextPacketId();
        body += char(id >> 8);
        body += char(id & 0xFF);
    }
    body += payload;
    send(header, body);
    return qos > 0 ? id : 0;
}

quint16 MqttLite::nextPacketId()
{
    if (m_nextPacketId == 0)      // 패킷 ID 0은 쓰면 안 됨 (규칙)
        m_nextPacketId = 1;
    return m_nextPacketId++;
}

void MqttLite::send(quint8 header, const QByteArray &body)
{
    if (m_socket->state() != QAbstractSocket::ConnectedState)
        return;
    QByteArray packet;
    packet += char(header);
    packet += encodeLength(body.size());
    packet += body;
    m_socket->write(packet);
}

QByteArray MqttLite::encodeLength(int length)
{
    QByteArray out;
    do {
        quint8 b = length % 128;
        length /= 128;
        if (length > 0)
            b |= 0x80;
        out += char(b);
    } while (length > 0);
    return out;
}

QByteArray MqttLite::encodeString(const QString &text)
{
    const QByteArray utf8 = text.toUtf8();
    QByteArray out;
    out += char(utf8.size() >> 8);
    out += char(utf8.size() & 0xFF);
    out += utf8;
    return out;
}
