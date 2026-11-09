"""Store MQTT v1.3 messages in the project's MySQL schema."""

import argparse
import json
import logging
import os
import time
from datetime import datetime
from decimal import Decimal
from getpass import getpass
from typing import Annotated, Literal

import mysql.connector
import paho.mqtt.client as mqtt
from mysql.connector import errorcode
from pydantic import BaseModel, BeforeValidator, Field, StringConstraints, ValidationError, model_validator

EVENTS = "server/events"
RESULTS = "server/results"
ENVIRONMENT = "sensor/env"
EMERGENCY_STOP = "event/estop"
COMMUNICATION_TOPICS = ("cmd/+", "res/+", "ui/cmd")
COMMUNICATION_KINDS = {"cmd": "COMMAND", "res": "RESPONSE", "ui": "OPERATOR_COMMAND"}
ELECTRICAL_STATES = {"measured": "COMPLETED", "skipped": "SKIPPED", "failed": "FAILED"}


def parse_timestamp(value):
    if not isinstance(value, str) or len(value) != 23:
        raise ValueError("expected YYYY-MM-DD HH:MM:SS.mmm in Korean local time")
    return datetime.strptime(value, "%Y-%m-%d %H:%M:%S.%f")


Timestamp = Annotated[datetime, BeforeValidator(parse_timestamp)]
RunId = Annotated[str, StringConstraints(pattern=r"^[0-9]{8}-[0-9]{6}-[0-9a-fA-F]{4}$")]
EventId = Annotated[str, StringConstraints(pattern=r"^[0-9a-fA-F]{32}$")]
SourceId = Annotated[str, StringConstraints(min_length=32, max_length=32)]
SourceName = Annotated[str, StringConstraints(min_length=1, max_length=64)]
Confidence = Annotated[Decimal, Field(ge=0, le=1)]
Voltage = Annotated[Decimal, Field(ge=Decimal("-9999.99999"), le=Decimal("9999.99999"))]
Resistance = Annotated[Decimal, Field(ge=Decimal("-999999.999999"), le=Decimal("999999.999999"))]
LoadResistance = Annotated[Decimal, Field(ge=0, le=Decimal("99999.99999"))]
SensorValue = Annotated[Decimal, Field(ge=Decimal("-99999999.999999"), le=Decimal("99999999.999999"))]


class Event(BaseModel):
    event_id: EventId
    run_id: RunId
    ts: Timestamp
    device: Annotated[str, StringConstraints(min_length=1, max_length=20)]
    level: Literal["INFO", "WARN", "ERROR"]
    message: Annotated[str, StringConstraints(max_length=255)]


class EmergencyStopEvent(BaseModel):
    event_id: SourceId
    run_id: RunId | None = None
    ts: Timestamp
    device: SourceName
    button_id: SourceName | None = None
    source: Literal["PHYSICAL_BUTTON", "WPF", "OTHER"]
    kind: Literal["BUTTON_PRESSED", "BUTTON_RELEASED", "STOP_CONFIRMED", "RESET_CONFIRMED"]
    details: Annotated[str, StringConstraints(max_length=512)] | None = None


class EnvironmentReading(BaseModel):
    sensor: SourceName
    metric: Literal["GAS", "TEMPERATURE", "HUMIDITY"]
    value: SensorValue
    unit: Annotated[str, StringConstraints(min_length=1, max_length=24)]


class EnvironmentSample(BaseModel):
    reading_id: SourceId
    run_id: RunId | None = None
    ts: Timestamp
    device: SourceName
    readings: Annotated[list[EnvironmentReading], Field(min_length=1)]


class Vision(BaseModel):
    status: Literal["ok", "failed"]
    result: Annotated[str, StringConstraints(min_length=1, max_length=64)] | None
    conf: Confidence | None
    polarity: Literal["minus_up", "plus_up", "unknown"] | None = None


class Result(BaseModel):
    run_id: RunId
    battery_id: Annotated[int, Field(ge=1, le=2**64 - 1)]
    pallet_cell: tuple[Annotated[int, Field(ge=0, le=1)], Annotated[int, Field(ge=0, le=4)]]
    picked_at: Timestamp
    vision: Vision
    electrical_status: Literal["measured", "skipped", "failed"]
    polarity: Literal["normal", "reversed", "abnormal"] | None = None
    v_open: Voltage | None
    v_load: Voltage | None
    r_int: Resistance | None
    r_load: LoadResistance | None = None
    grade: Literal["A", "B", "NG", "RESORT"]
    reason: str | None
    dest: Literal["AGV", "B_BOX", "NG_BOX", "RESORT_BOX"] | None
    finished_at: Timestamp
    rule_version: Annotated[str, StringConstraints(min_length=1, max_length=40)] | None = None

    @model_validator(mode="after")
    def validate_measurements(self):
        if self.electrical_status == "measured":
            if self.polarity == "reversed":
                # Older queued messages may still include a clipped open-circuit voltage.
                self.v_open = None
            elif self.v_open is None:
                raise ValueError("measured results require open-circuit voltage")
            if self.r_load is not None and self.polarity != "normal":
                raise ValueError("load resistance requires normal polarity")
            # Older queued v1 results can omit polarity even when they carry load data.
            if "polarity" in self.model_fields_set and self.polarity != "normal":
                if self.v_load is not None or self.r_int is not None:
                    raise ValueError("load voltage and internal resistance require normal polarity")
        elif any(value is not None for value in (self.polarity, self.v_open, self.v_load, self.r_int, self.r_load)):
            raise ValueError("skipped or failed measurements must have null measurement fields")
        return self


MESSAGE_MODELS = {
    EVENTS: Event,
    RESULTS: Result,
    ENVIRONMENT: EnvironmentSample,
    EMERGENCY_STOP: EmergencyStopEvent,
}


def parse_message(topic, payload):
    message = MESSAGE_MODELS[topic].model_validate_json(payload, strict=True)
    if not isinstance(message, (Event, Result)):
        return message, None
    started_at = datetime.strptime(message.run_id[:15], "%Y%m%d-%H%M%S")
    return message, started_at


def insert_once(cursor, statement, parameters):
    try:
        cursor.execute(statement, parameters)
    except mysql.connector.IntegrityError as error:
        if error.errno != errorcode.ER_DUP_ENTRY:
            raise
        return False
    return True


def insert_result(cursor, run_id, result):
    rule_id = None
    if result.rule_version is not None:
        # The result names the rule version without providing its settings or effective date.
        cursor.execute("""
            INSERT INTO grading_rules (rule_version) VALUES (%s)
            ON DUPLICATE KEY UPDATE rule_id = LAST_INSERT_ID(rule_id)
            """, (result.rule_version,))
        rule_id = cursor.lastrowid

    row, column = result.pallet_cell
    # The DB uses one-based row-major slots. For example, [0, 3] is slot 4.
    if not insert_once(cursor, """
        INSERT INTO battery_cycles (
            run_id, source_battery_id, rule_id, pallet_slot, started_at, processing_status,
            electrical_status, final_grade, result_reason, finished_at, sorted_destination
        ) VALUES (%s, %s, %s, %s, %s, 'FINALIZED', %s, %s, %s, %s, %s)
        """, (run_id, result.battery_id, rule_id, row * 5 + column + 1, result.picked_at,
              ELECTRICAL_STATES[result.electrical_status], result.grade, result.reason,
              result.finished_at, result.dest)):
        return False
    cycle_id = cursor.lastrowid

    vision = result.vision
    if vision.status == "failed" or vision.result is None:
        visual_result = "ERROR"
    else:
        visual_result = "PASS" if vision.result == "normal" else "FAIL"
    cursor.execute("""
        INSERT INTO visual_inspections (cycle_id, visual_result, polarity, confidence, inspected_at)
        VALUES (%s, %s, %s, %s, NULL)
        """, (cycle_id, visual_result, vision.polarity, vision.conf))
    if visual_result == "FAIL":
        cursor.execute("""
            INSERT INTO visual_defects (visual_inspection_id, defect_type, confidence)
            VALUES (%s, %s, %s)
            """, (cursor.lastrowid, vision.result, vision.conf))

    if result.electrical_status == "measured":
        cursor.execute("""
            INSERT INTO electrical_tests (
                cycle_id, polarity, no_load_voltage_v, loaded_voltage_v,
                estimated_internal_resistance_ohm, load_resistance_ohm, measured_at
            ) VALUES (%s, %s, %s, %s, %s, %s, NULL)
            """, (cycle_id, result.polarity, result.v_open, result.v_load, result.r_int, result.r_load))
    return True


def insert_line_message(cursor, message, started_at):
    cursor.execute("""
        INSERT INTO runs (source_run_id, run_type, started_at)
        VALUES (%s, 'LINE', %s)
        ON DUPLICATE KEY UPDATE run_id = LAST_INSERT_ID(run_id)
        """, (message.run_id.lower(), started_at))
    run_id = cursor.lastrowid

    if isinstance(message, Event):
        return insert_once(cursor, """
            INSERT INTO equipment_events (
                source_event_id, run_id, equipment_name, event_type, severity, details, occurred_at
            ) VALUES (%s, %s, %s, 'DEVICE_STATUS_CHANGED', %s, %s, %s)
            """, (message.event_id.lower(), run_id, message.device, message.level, message.message, message.ts))
    return insert_result(cursor, run_id, message)


def insert_environment_readings(cursor, sample):
    source_run_id = sample.run_id.lower() if sample.run_id else None
    inserted = False
    for reading in sample.readings:
        if insert_once(cursor, """
            INSERT INTO environment_readings (
                source_reading_id, source_run_id, device_name, sensor_name,
                metric, reading_value, unit, measured_at
            ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
            """, (sample.reading_id, source_run_id, sample.device, reading.sensor,
                  reading.metric, reading.value, reading.unit, sample.ts)):
            inserted = True
    return inserted


def insert_emergency_stop(cursor, event):
    source_run_id = event.run_id.lower() if event.run_id else None
    return insert_once(cursor, """
        INSERT INTO emergency_stop_events (
            source_event_id, source_run_id, device_name, button_id,
            trigger_source, event_kind, details, occurred_at
        ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
        """, (event.event_id, source_run_id, event.device, event.button_id,
              event.source, event.kind, event.details, event.ts))


def save_message(connection, message, started_at):
    with connection.cursor() as cursor:
        if isinstance(message, EnvironmentSample):
            inserted = insert_environment_readings(cursor, message)
        elif isinstance(message, EmergencyStopEvent):
            inserted = insert_emergency_stop(cursor, message)
        else:
            inserted = insert_line_message(cursor, message, started_at)

    if inserted:
        connection.commit()
    else:
        connection.rollback()
    return inserted


def log_text(payload, key, max_length):
    value = payload.get(key)
    if not isinstance(value, str) or not 0 < len(value) <= max_length:
        return None
    return value.encode("utf-8", errors="replace").decode("utf-8")


def save_communication_log(connection, received):
    topic_group, topic_device = received.topic.split("/", 1)
    kind = COMMUNICATION_KINDS[topic_group]
    device = "ui" if kind == "OPERATOR_COMMAND" else topic_device[:255]
    payload_text = received.payload.decode("utf-8", errors="replace")
    try:
        payload = json.loads(payload_text)
    except (ValueError, RecursionError):
        payload = {}
    if not isinstance(payload, dict):
        payload = {}

    sent_at = None
    try:
        timestamp = parse_timestamp(payload.get("ts"))
        if timestamp.year >= 1000:
            sent_at = timestamp
    except ValueError:
        pass

    battery_id = payload.get("battery_id")
    if type(battery_id) is not int or not 1 <= battery_id <= 2**64 - 1:
        battery_id = None

    # Each delivery is a diagnostic record, including retransmissions.
    with connection.cursor() as cursor:
        cursor.execute("""
            INSERT INTO communication_logs (
                topic, message_kind, device, cmd_id, source_run_id, source_battery_id,
                action, response_status, error_text, sent_at, mqtt_qos, mqtt_dup,
                mqtt_retained, payload_text
            ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            """, (received.topic, kind, device, log_text(payload, "cmd_id", 64),
                  log_text(payload, "run_id", 20), battery_id, log_text(payload, "action", 64),
                  log_text(payload, "status", 32), log_text(payload, "error", 512), sent_at,
                  received.qos, received.dup, received.retain, payload_text))
    connection.commit()


def on_connect(client, userdata, flags, reason_code, properties):
    if reason_code.is_failure:
        logging.error("MQTT connection refused: %s", reason_code)
        return
    client.subscribe([(topic, 1) for topic in (*MESSAGE_MODELS, *COMMUNICATION_TOPICS)])


def on_subscribe(client, userdata, mid, reason_codes, properties):
    if any(code.is_failure for code in reason_codes):
        logging.error("MQTT subscription rejected: %s", reason_codes)
    else:
        logging.info("Subscribed to %s", ", ".join((*MESSAGE_MODELS, *COMMUNICATION_TOPICS)))


def on_message(client, db_options, received):
    is_structured_message = received.topic in MESSAGE_MODELS
    try:
        if is_structured_message:
            message, started_at = parse_message(received.topic, received.payload)
        with mysql.connector.connect(**db_options) as connection:
            if is_structured_message:
                inserted = save_message(connection, message, started_at)
                logging.info("%s %s for run %s", "Saved" if inserted else "Duplicate", received.topic, message.run_id)
            else:
                save_communication_log(connection, received)
                logging.info("Logged MQTT %s", received.topic)
    except ValidationError as error:
        logging.error("Rejected malformed %s: %s", received.topic, error.errors(include_input=False))
    except (KeyError, ValueError) as error:
        logging.error("Rejected malformed %s: %s", received.topic, error)
    except mysql.connector.Error:
        logging.exception("MySQL save failed. Reconnecting without acknowledging %s", received.topic)
        client.disconnect()
        return

    # Valid messages reach here only after commit or after confirming a duplicate.
    if client.ack(received.mid, received.qos) != mqtt.MQTT_ERR_SUCCESS:
        logging.warning("Could not acknowledge %s; reconnecting", received.topic)
        client.disconnect()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--broker", default="127.0.0.1")
    parser.add_argument("--mqtt-port", type=int, default=1883)
    parser.add_argument("--client-id", default="aa-battery-db-writer-v1")
    parser.add_argument("--db-host", default="127.0.0.1")
    parser.add_argument("--db-port", type=int, default=3306)
    parser.add_argument("--db-user", default="root")
    parser.add_argument("--database", default="aa_battery_inspection")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    password = os.environ.get("AA_DB_PASSWORD")
    if password is None:
        password = getpass("MySQL password: ")
    db_options = dict(
        host=args.db_host, port=args.db_port, user=args.db_user, password=password,
        database=args.database, charset="utf8mb4", autocommit=False,
        connection_timeout=5, read_timeout=5, write_timeout=5,
    )
    client = mqtt.Client(
        mqtt.CallbackAPIVersion.VERSION2, client_id=args.client_id, clean_session=False,
        userdata=db_options, protocol=mqtt.MQTTv311, manual_ack=True,
    )
    if mqtt_user := os.environ.get("AA_MQTT_USER"):
        client.username_pw_set(mqtt_user, os.environ.get("AA_MQTT_PASSWORD"))
    client.on_connect = on_connect
    client.on_subscribe = on_subscribe
    client.on_message = on_message

    try:
        while True:
            try:
                client.connect(args.broker, args.mqtt_port)
                client.loop_forever()
            except OSError as error:
                logging.warning("MQTT connection failed: %s", error)
            time.sleep(3)
    except KeyboardInterrupt:
        client.disconnect()


if __name__ == "__main__":
    main()
