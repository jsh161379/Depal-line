"""Store MQTT v1.3 line messages and order-management messages in the project's MySQL schema."""

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
# Order management, as in 주문_통신_규약_v1.0 chapters 3-4 (2026-11-17).
# The homepage account is public, so only messages the order program has checked are stored:
# order/accepted and inquiry/accepted, never order/new or inquiry/new.
ORDER_ACCEPTED = "order/accepted"
ORDER_STATUS = "order/status/+"  # retained; the last level is the order number
ORDER_STOCK = "order/stock"
ORDER_RUN = "order/run"
INQUIRY_ACCEPTED = "inquiry/accepted"
INQUIRY_ANSWER = "inquiry/answer/+"  # retained; the last level is the inquiry number
# Subscribed by the first 11-17 draft. A persistent session would keep them, so they are dropped on connect.
LEGACY_ORDER_TOPICS = ("order/new", "inquiry/new", "inquiry/answer")
ORDER_TABLES = ("orders", "order_items", "order_status_log", "order_runs", "stock", "stock_log", "inquiries")
FINAL_ORDER_STATUSES = ("delivered", "canceled")
# Notification topics kept as raw text in communication_logs (message_kind EVENT), with the sending device.
EVENT_LOG_TOPICS = {"vision/result": "yolo", "event/mega": "mega"}
COMMUNICATION_TOPICS = ("cmd/+", "res/+", "ui/cmd", *EVENT_LOG_TOPICS)
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
    # Admin screens report as "ui-<PC name>", which can exceed 20 characters. equipment_name is varchar(64).
    device: Annotated[str, StringConstraints(min_length=1, max_length=64)]
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


class VisionDefect(BaseModel):
    type: Annotated[str, StringConstraints(min_length=1, max_length=64)]
    conf: Confidence | None = None
    cam: Annotated[str, StringConstraints(min_length=1, max_length=16)] | None = None


class Vision(BaseModel):
    status: Literal["ok", "failed"]
    # New messages use "normal" / "defect" and list the defect types in `defects`.
    # Older messages put a single defect name (for example "scratch") here and omit `defects`.
    result: Annotated[str, StringConstraints(min_length=1, max_length=64)] | None
    conf: Confidence | None
    polarity: Literal["minus_up", "plus_up", "unknown"] | None = None
    defects: list[VisionDefect] | None = None

    @model_validator(mode="after")
    def validate_defects(self):
        if self.result == "normal" and self.defects:
            raise ValueError("a normal vision result must not list defects")
        return self


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
    # NG is kept for older data. New results use NG_VISUAL (appearance) or NG_VOLTAGE (voltage/resistance).
    grade: Literal["A", "B", "NG", "RESORT", "NG_VISUAL", "NG_VOLTAGE"]
    reason: str | None
    # NG_BOX is kept for older data. New results use NG_VISUAL_BOX or NG_VOLTAGE_BOX.
    dest: Literal["AGV", "B_BOX", "NG_BOX", "RESORT_BOX", "NG_VISUAL_BOX", "NG_VOLTAGE_BOX"] | None
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


OrderNo = Annotated[str, StringConstraints(pattern=r"^BR-[0-9]{8}-[0-9A-Za-z]{4}$")]
InquiryNo = Annotated[str, StringConstraints(pattern=r"^Q-[0-9]{8}-[0-9A-Za-z]{4}$")]
OrderStatusName = Literal["received", "inspect", "shipping", "delivered", "canceled"]
ProductId = Annotated[str, StringConstraints(min_length=1, max_length=10)]
PersonName = Annotated[str, StringConstraints(min_length=1, max_length=50)]
Phone = Annotated[str, StringConstraints(min_length=1, max_length=20)]
Won = Annotated[int, Field(ge=0, le=2**32 - 1)]
Packs = Annotated[int, Field(ge=1, le=2**31 - 1)]
LongText = Annotated[str, StringConstraints(min_length=1, max_length=10000)]  # TEXT holds 65,535 bytes
Operator = Annotated[str, StringConstraints(min_length=1, max_length=50)] | None


class OrderItem(BaseModel):
    id: ProductId  # product id: aa4 / aa12 / aa24 / aar2
    name: Annotated[str, StringConstraints(min_length=1, max_length=50)]
    qty: Packs
    price: Won


class OrderAccepted(BaseModel):
    # The order/new message as the homepage sent it, republished by the order program after its checks.
    no: OrderNo
    ts: Timestamp  # when the customer ordered
    accepted_at: Timestamp | None = None  # when the order program accepted it
    name: PersonName
    phone: Phone
    addr: Annotated[str, StringConstraints(min_length=1, max_length=255)]
    memo: Annotated[str, StringConstraints(max_length=255)] | None = None  # "" is stored as NULL
    items: Annotated[list[OrderItem], Field(min_length=1)]
    total: Won


class OrderStatusChange(BaseModel):
    # Published retained, so it is delivered again after every reconnect (stored once by the unique key).
    # cert_no (inspection certificate number) is also sent; its format is not decided yet, so it is not stored.
    no: OrderNo
    ts: Timestamp
    status: OrderStatusName
    # Omitted, null or "" keeps the stored courier / tracking number.
    courier: Annotated[str, StringConstraints(max_length=30)] | None = None
    tracking: Annotated[str, StringConstraints(max_length=30)] | None = None
    by: Operator = None  # who changed the status


class StockChange(BaseModel):
    event_id: EventId
    ts: Timestamp
    product_id: ProductId
    # restock: add `packs`. adjust: set the warehouse count to `packs`.
    kind: Literal["restock", "adjust"]
    packs: Annotated[int, Field(ge=0, le=2**31 - 1)]
    by: Operator = None

    @model_validator(mode="after")
    def validate_packs(self):
        if self.kind == "restock" and self.packs == 0:
            raise ValueError("restock needs at least one pack")
        return self


class OrderRun(BaseModel):
    no: OrderNo
    run_id: RunId
    battery_count: Annotated[int, Field(ge=0, le=2**32 - 1)]
    ts: Timestamp


class InquiryAccepted(BaseModel):
    no: InquiryNo
    ts: Timestamp
    name: PersonName
    phone: Phone
    type: Literal["delivery", "cancel", "product", "bulk", "etc"]
    order_no: Annotated[str, StringConstraints(min_length=1, max_length=20)] | None = None
    question: LongText


class InquiryAnswer(BaseModel):
    # Published retained, so the latest answer is delivered again after every reconnect.
    no: InquiryNo
    answer: LongText
    by: Operator = None
    ts: Timestamp


class Rejected(ValueError):
    """A well-formed message that the stored data does not allow (for example an unknown order)."""


LINE_MODELS = {
    EVENTS: Event,
    RESULTS: Result,
    ENVIRONMENT: EnvironmentSample,
    EMERGENCY_STOP: EmergencyStopEvent,
}
ORDER_MODELS = {
    ORDER_ACCEPTED: OrderAccepted,
    ORDER_STATUS: OrderStatusChange,
    ORDER_STOCK: StockChange,
    ORDER_RUN: OrderRun,
    INQUIRY_ACCEPTED: InquiryAccepted,
    INQUIRY_ANSWER: InquiryAnswer,
}
MESSAGE_MODELS = {**LINE_MODELS, **ORDER_MODELS}


def topic_matches(subscription, topic):
    """MQTT matching for the single-level '+' wildcard used in this program."""
    sub_levels, topic_levels = subscription.split("/"), topic.split("/")
    return len(sub_levels) == len(topic_levels) and all(
        s == "+" or s == t for s, t in zip(sub_levels, topic_levels))


def subscription_for(topic):
    """The MESSAGE_MODELS key that covers this topic, or None."""
    if topic in MESSAGE_MODELS:
        return topic
    return next((sub for sub in MESSAGE_MODELS if "+" in sub and topic_matches(sub, topic)), None)


def parse_message(topic, payload):
    subscription = subscription_for(topic)
    message = MESSAGE_MODELS[subscription].model_validate_json(payload, strict=True)
    if subscription in (ORDER_STATUS, INQUIRY_ANSWER) and topic.rsplit("/", 1)[1] != message.no:
        raise ValueError(f"topic number does not match no {message.no}")
    if not isinstance(message, (Event, Result)):
        return message, None
    started_at = datetime.strptime(message.run_id[:15], "%Y%m%d-%H%M%S")
    return message, started_at


def message_label(message):
    """Short identifier for log lines."""
    for field in ("run_id", "no", "order_no", "inquiry_id", "product_id", "reading_id", "event_id"):
        value = getattr(message, field, None)
        if value:
            return f"{field} {value}"
    return ""


def insert_once(cursor, statement, parameters):
    try:
        cursor.execute(statement, parameters)
    except mysql.connector.IntegrityError as error:
        if error.errno != errorcode.ER_DUP_ENTRY:
            raise
        return False
    return True


def defect_rows(vision):
    """(defect_type, confidence, camera) rows for a failed visual inspection."""
    if vision.defects is not None:
        return [(defect.type, defect.conf, defect.cam) for defect in vision.defects]
    if vision.result != "defect":
        # Older format: the defect name is the result itself.
        return [(vision.result, vision.conf, None)]
    return []


def insert_result(cursor, run_id, result, has_camera):
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
        visual_inspection_id = cursor.lastrowid
        for defect_type, confidence, camera in defect_rows(vision):
            if has_camera:
                cursor.execute("""
                    INSERT INTO visual_defects (visual_inspection_id, defect_type, confidence, camera)
                    VALUES (%s, %s, %s, %s)
                    """, (visual_inspection_id, defect_type, confidence, camera))
            else:
                # The DB has not been given the camera column yet; store the rest.
                cursor.execute("""
                    INSERT INTO visual_defects (visual_inspection_id, defect_type, confidence)
                    VALUES (%s, %s, %s)
                    """, (visual_inspection_id, defect_type, confidence))

    if result.electrical_status == "measured":
        cursor.execute("""
            INSERT INTO electrical_tests (
                cycle_id, polarity, no_load_voltage_v, loaded_voltage_v,
                estimated_internal_resistance_ohm, load_resistance_ohm, measured_at
            ) VALUES (%s, %s, %s, %s, %s, %s, NULL)
            """, (cycle_id, result.polarity, result.v_open, result.v_load, result.r_int, result.r_load))
    return True


def insert_line_message(cursor, message, started_at, has_camera):
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
    return insert_result(cursor, run_id, message, has_camera)


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


def insert_order(cursor, order):
    if not insert_once(cursor, """
        INSERT INTO orders (
            order_no, received_at, accepted_at, customer_name, customer_phone, shipping_address,
            memo, total_amount, order_status, updated_at
        ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, 'received', %s)
        """, (order.no, order.ts, order.accepted_at, order.name, order.phone, order.addr,
              order.memo or None, order.total, order.ts)):
        return False  # the order number already exists
    for item in order.items:
        cursor.execute("""
            INSERT INTO order_items (order_no, product_id, product_name, quantity, unit_price)
            VALUES (%s, %s, %s, %s, %s)
            """, (order.no, item.id, item.name, item.qty, item.price))
    cursor.execute("""
        INSERT INTO order_status_log (order_no, from_status, to_status, changed_at, changed_by)
        VALUES (%s, NULL, 'received', %s, NULL)
        """, (order.no, order.ts))
    return True


def change_stock(cursor, product_id, delta, reason, at, order_no=None, changed_by=None, source_event_id=None):
    """Add delta packs to a product (creating its stock row) and log the change."""
    cursor.execute("SELECT stock_packs FROM stock WHERE product_id = %s FOR UPDATE", (product_id,))
    row = cursor.fetchone()
    if row is None:
        packs_after = delta
        cursor.execute("INSERT INTO stock (product_id, stock_packs, updated_at) VALUES (%s, %s, %s)",
                       (product_id, packs_after, at))
    else:
        packs_after = row[0] + delta
        cursor.execute("UPDATE stock SET stock_packs = %s, updated_at = %s WHERE product_id = %s",
                       (packs_after, at, product_id))
    if packs_after < 0:
        logging.warning("Stock of %s is now %d packs (more shipped than recorded)", product_id, packs_after)
    cursor.execute("""
        INSERT INTO stock_log (
            source_event_id, product_id, delta_packs, packs_after, reason, order_no, changed_by, logged_at
        ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
        """, (source_event_id, product_id, delta, packs_after, reason, order_no, changed_by, at))


def update_order_status(cursor, change):
    cursor.execute("SELECT order_status, updated_at FROM orders WHERE order_no = %s FOR UPDATE",
                   (change.no,))
    row = cursor.fetchone()
    if row is None:
        raise Rejected(f"unknown order {change.no}")
    current, updated_at = row

    # A redelivered message hits the unique key here and is reported as a duplicate.
    if not insert_once(cursor, """
        INSERT INTO order_status_log (order_no, from_status, to_status, changed_at, changed_by)
        VALUES (%s, %s, %s, %s, %s)
        """, (change.no, current, change.status, change.ts, change.by)):
        return False
    if change.ts < updated_at:
        raise Rejected(f"{change.no} changed at {updated_at}, after this message ({change.ts})")
    if current in FINAL_ORDER_STATUSES and change.status != current:
        raise Rejected(f"{change.no} is already {current}")

    cursor.execute("""
        UPDATE orders SET order_status = %s, courier = COALESCE(%s, courier),
            tracking_no = COALESCE(%s, tracking_no), updated_at = %s
        WHERE order_no = %s
        """, (change.status, change.courier or None, change.tracking or None, change.ts, change.no))

    if change.status == "delivered" and current != "delivered":
        cursor.execute("""
            SELECT product_id, SUM(quantity) FROM order_items WHERE order_no = %s
            GROUP BY product_id ORDER BY product_id
            """, (change.no,))
        for product_id, quantity in cursor.fetchall():
            change_stock(cursor, product_id, -int(quantity), "delivered", change.ts,
                         order_no=change.no, changed_by=change.by)
    return True


def insert_stock_change(cursor, change):
    cursor.execute("SELECT 1 FROM stock_log WHERE source_event_id = %s", (change.event_id.lower(),))
    if cursor.fetchone() is not None:
        return False
    if change.kind == "restock":
        delta = change.packs
    else:
        cursor.execute("SELECT stock_packs FROM stock WHERE product_id = %s FOR UPDATE", (change.product_id,))
        row = cursor.fetchone()
        delta = change.packs - (row[0] if row else 0)
    change_stock(cursor, change.product_id, delta, change.kind, change.ts,
                 changed_by=change.by, source_event_id=change.event_id.lower())
    return True


def require_order(cursor, order_no):
    cursor.execute("SELECT 1 FROM orders WHERE order_no = %s", (order_no,))
    if cursor.fetchone() is None:
        raise Rejected(f"unknown order {order_no}")


def upsert_order_run(cursor, link):
    require_order(cursor, link.no)
    # A later message for the same order and run replaces the battery count.
    cursor.execute("""
        INSERT INTO order_runs (order_no, source_run_id, battery_count, linked_at)
        VALUES (%s, %s, %s, %s) AS new
        ON DUPLICATE KEY UPDATE battery_count = new.battery_count, linked_at = new.linked_at
        """, (link.no, link.run_id.lower(), link.battery_count, link.ts))
    return True


def insert_inquiry(cursor, inquiry):
    return insert_once(cursor, """
        INSERT INTO inquiries (
            inquiry_id, asked_at, customer_name, customer_phone, inquiry_type, order_no, question
        ) VALUES (%s, %s, %s, %s, %s, %s, %s)
        """, (inquiry.no, inquiry.ts, inquiry.name, inquiry.phone, inquiry.type,
              inquiry.order_no, inquiry.question))


def answer_inquiry(cursor, answer):
    cursor.execute("SELECT answered_at FROM inquiries WHERE inquiry_id = %s FOR UPDATE", (answer.no,))
    row = cursor.fetchone()
    if row is None:
        raise Rejected(f"unknown inquiry {answer.no}")
    if row[0] is not None and answer.ts <= row[0]:
        return False  # the retained answer redelivered after a reconnect, or an older one
    # A corrected answer is published again and replaces the previous one.
    cursor.execute("""
        UPDATE inquiries SET answer = %s, answered_at = %s, answered_by = %s WHERE inquiry_id = %s
        """, (answer.answer, answer.ts, answer.by, answer.no))
    return True


ORDER_WRITERS = {
    OrderAccepted: insert_order,
    OrderStatusChange: update_order_status,
    StockChange: insert_stock_change,
    OrderRun: upsert_order_run,
    InquiryAccepted: insert_inquiry,
    InquiryAnswer: answer_inquiry,
}


def save_message(connection, message, started_at, has_camera=False):
    try:
        # Buffered so a SELECT read with fetchone() never leaves unread rows behind.
        with connection.cursor(buffered=True) as cursor:
            if isinstance(message, EnvironmentSample):
                inserted = insert_environment_readings(cursor, message)
            elif isinstance(message, EmergencyStopEvent):
                inserted = insert_emergency_stop(cursor, message)
            elif type(message) in ORDER_WRITERS:
                inserted = ORDER_WRITERS[type(message)](cursor, message)
            else:
                inserted = insert_line_message(cursor, message, started_at, has_camera)
    except Rejected:
        connection.rollback()
        raise

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


def communication_kind(topic):
    """(message_kind, device) for a topic stored in communication_logs."""
    if topic in EVENT_LOG_TOPICS:
        return "EVENT", EVENT_LOG_TOPICS[topic]
    topic_group, topic_device = topic.split("/", 1)
    kind = COMMUNICATION_KINDS[topic_group]
    return kind, "ui" if kind == "OPERATOR_COMMAND" else topic_device[:255]


def save_communication_log(connection, received):
    kind, device = communication_kind(received.topic)
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


class Database:
    """One MySQL connection reused across messages, reopened after an error."""

    def __init__(self, options):
        self.options = options
        self.connection = None
        self.has_camera = False
        self.has_event_kind = False
        # Assume the order tables exist until a connection shows otherwise.
        self.has_orders = True

    def connect(self):
        if self.connection is not None and self.connection.is_connected():
            return self.connection
        self.close()
        self.connection = mysql.connector.connect(**self.options)
        self.detect_schema()
        return self.connection

    def detect_schema(self):
        # Optional columns added on 2026-11-10. Older databases keep working without them.
        with self.connection.cursor() as cursor:
            cursor.execute("""
                SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = %s AND (
                    (TABLE_NAME = 'visual_defects' AND COLUMN_NAME = 'camera') OR
                    (TABLE_NAME = 'communication_logs' AND COLUMN_NAME = 'message_kind'))
                """, (self.options["database"],))
            rows = [tuple(value.decode() if isinstance(value, (bytes, bytearray)) else value for value in row)
                    for row in cursor.fetchall()]
            # Order tables added on 2026-11-17.
            cursor.execute(f"""
                SELECT TABLE_NAME FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = %s AND TABLE_NAME IN ({", ".join(["%s"] * len(ORDER_TABLES))})
                """, (self.options["database"], *ORDER_TABLES))
            order_tables = {value.decode() if isinstance(value, (bytes, bytearray)) else value
                            for value, in cursor.fetchall()}
        self.connection.commit()
        missing_orders = [table for table in ORDER_TABLES if table not in order_tables]
        self.has_orders = not missing_orders
        if missing_orders:
            logging.warning("Order tables missing (%s); order and inquiry topics will not be subscribed",
                            ", ".join(missing_orders))
        self.has_camera = any(column == "camera" for _, column, _ in rows)
        self.has_event_kind = any(column == "message_kind" and "'EVENT'" in str(column_type)
                                  for _, column, column_type in rows)
        if not self.has_camera:
            logging.warning("visual_defects.camera is missing; defect cameras will not be stored")
        if not self.has_event_kind:
            logging.warning("communication_logs.message_kind has no EVENT; %s will not be logged",
                            ", ".join(EVENT_LOG_TOPICS))

    def close(self):
        if self.connection is not None:
            try:
                self.connection.close()
            except mysql.connector.Error:
                pass
        self.connection = None


def subscribed_topics(db):
    order_topics = tuple(ORDER_MODELS) if db.has_orders else ()
    return (*LINE_MODELS, *order_topics, *COMMUNICATION_TOPICS)


def on_connect(client, db, flags, reason_code, properties):
    if reason_code.is_failure:
        logging.error("MQTT connection refused: %s", reason_code)
        return
    try:
        db.connect()  # reads which optional tables exist before choosing topics
    except mysql.connector.Error as error:
        logging.warning("MySQL not reachable yet (%s); subscribing to every topic", error)
    # A persistent session keeps old subscriptions, so drop the ones this version does not want.
    client.unsubscribe(list(LEGACY_ORDER_TOPICS) if db.has_orders else [*LEGACY_ORDER_TOPICS, *ORDER_MODELS])
    client.subscribe([(topic, 1) for topic in subscribed_topics(db)])


def on_subscribe(client, db, mid, reason_codes, properties):
    if any(code.is_failure for code in reason_codes):
        logging.error("MQTT subscription rejected: %s", reason_codes)
    else:
        logging.info("Subscribed to %s", ", ".join(subscribed_topics(db)))


def on_message(client, db, received):
    is_structured_message = subscription_for(received.topic) is not None
    try:
        if is_structured_message:
            message, started_at = parse_message(received.topic, received.payload)
        connection = db.connect()
        if is_structured_message and type(message) in ORDER_WRITERS and not db.has_orders:
            # Delivered before the unsubscribe took effect; the order tables are not there to hold it.
            logging.warning("Dropped %s: order tables are missing", received.topic)
        elif is_structured_message:
            inserted = save_message(connection, message, started_at, db.has_camera)
            logging.info("%s %s (%s)", "Saved" if inserted else "Duplicate", received.topic, message_label(message))
        elif received.topic in EVENT_LOG_TOPICS and not db.has_event_kind:
            pass  # The DB cannot store EVENT rows yet; detect_schema has already warned.
        else:
            save_communication_log(connection, received)
            logging.info("Logged MQTT %s", received.topic)
    except ValidationError as error:
        logging.error("Rejected malformed %s: %s", received.topic, error.errors(include_input=False))
    except Rejected as error:
        logging.error("Rejected %s: %s", received.topic, error)
    except (KeyError, ValueError) as error:
        logging.error("Rejected malformed %s: %s", received.topic, error)
    except mysql.connector.Error:
        logging.exception("MySQL save failed. Reconnecting without acknowledging %s", received.topic)
        db.close()
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
    db = Database(db_options)
    client = mqtt.Client(
        mqtt.CallbackAPIVersion.VERSION2, client_id=args.client_id, clean_session=False,
        userdata=db, protocol=mqtt.MQTTv311, manual_ack=True,
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
        db.close()


if __name__ == "__main__":
    main()
