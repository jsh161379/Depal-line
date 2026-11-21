using BareunOrder.Core.Models;
using MySqlConnector;

namespace BareunOrder.Core.Data;

/// <summary>
/// 켤 때 MySQL에서 지난 주문·문의를 읽어 온다 (읽기 전용 계정 ui_reader).
/// - 저장은 여전히 DB 저장 프로그램(mqtt_db_writer.py)이 한다. 여기는 읽기만.
/// - 읽는 범위: 최근 N일(설정 DbLoadDays) + 그보다 오래됐어도 아직 안 끝난 주문 / 답변 안 한 문의
/// - 표 이름·열 이름은 DB 담당의 DB_변경_2026-11-17_주문.sql 기준
/// - 고객 정보 칸(customer_type·age_group·region, DB_변경_2026-11-20_고객정보.sql)은 DB에 있을 때만 읽는다
/// </summary>
public static class OrderDbReader
{
    public sealed record Result(IReadOnlyList<Order> Orders, IReadOnlyList<Inquiry> Inquiries);

    private const string OrderWhere =
        "(o.received_at >= NOW() - INTERVAL @days DAY OR o.order_status IN ('received','inspect','shipping'))";

    public static async Task<Result> LoadAsync(string connectionString, int days, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // ① 주문 상품 줄 (주문번호별로 모음)
        var items = new Dictionary<string, List<OrderItem>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT i.order_no, i.product_id, i.product_name, i.quantity, i.unit_price " +
                "FROM order_items i JOIN orders o ON o.order_no = i.order_no " +
                $"WHERE {OrderWhere} ORDER BY i.order_item_id";
            cmd.Parameters.AddWithValue("@days", days);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var no = r.GetString(0);
                if (!items.TryGetValue(no, out var list)) items[no] = list = new List<OrderItem>();
                list.Add(new OrderItem(r.GetString(1), r.GetString(2), Int(r, 3), Int(r, 4)));
            }
        }

        // 고객 정보 칸이 있는 DB인지 (11/20 SQL을 아직 안 돌린 PC에서도 켜지게)
        bool hasCustomer;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND COLUMN_NAME IN ('customer_type','age_group','region')";
            hasCustomer = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 3;
        }

        // ② 주문
        var orders = new List<Order>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT o.order_no, o.received_at, o.accepted_at, o.customer_name, o.customer_phone, " +
                "o.shipping_address, o.memo, o.total_amount, o.order_status, o.courier, o.tracking_no, o.cert_no" +
                (hasCustomer ? ", o.customer_type, o.age_group, o.region " : " ") +
                $"FROM orders o WHERE {OrderWhere} ORDER BY o.received_at DESC";
            cmd.Parameters.AddWithValue("@days", days);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var no = r.GetString(0);
                var (ctype, age, region) = hasCustomer
                    ? CustomerInfo.Normalize(Opt(r, 12), Opt(r, 13), Opt(r, 14))
                    : CustomerInfo.Normalize(null, null, null);
                orders.Add(new Order
                {
                    No = no,
                    ReceivedAt = r.GetDateTime(1),
                    AcceptedAt = r.IsDBNull(2) ? null : r.GetDateTime(2),
                    Name = r.GetString(3),
                    Phone = r.GetString(4),
                    Address = r.GetString(5),
                    Memo = r.IsDBNull(6) ? "" : r.GetString(6),
                    Total = Int(r, 7),
                    Status = OrderStatusText.FromWire(r.GetString(8)),
                    Courier = r.IsDBNull(9) ? null : r.GetString(9),
                    Tracking = r.IsDBNull(10) ? null : r.GetString(10),
                    CertNo = r.IsDBNull(11) ? null : r.GetString(11),
                    Items = items.TryGetValue(no, out var list) ? list : Array.Empty<OrderItem>(),
                    CustomerType = ctype,
                    AgeGroup = age,
                    Region = region,
                });
            }
        }

        // ③ 문의
        var inquiries = new List<Inquiry>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT inquiry_id, asked_at, customer_name, customer_phone, inquiry_type, order_no, " +
                "question, answer, answered_at, answered_by FROM inquiries " +
                "WHERE asked_at >= NOW() - INTERVAL @days DAY OR answer IS NULL OR answer = '' " +
                "ORDER BY asked_at DESC";
            cmd.Parameters.AddWithValue("@days", days);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                inquiries.Add(new Inquiry
                {
                    No = r.GetString(0),
                    AskedAt = r.GetDateTime(1),
                    Name = r.GetString(2),
                    Phone = r.GetString(3),
                    Type = r.GetString(4),
                    OrderNo = r.IsDBNull(5) ? null : r.GetString(5),
                    Question = r.GetString(6),
                    Answer = r.IsDBNull(7) ? null : r.GetString(7),
                    AnsweredAt = r.IsDBNull(8) ? null : r.GetDateTime(8),
                    AnsweredBy = r.IsDBNull(9) ? null : r.GetString(9),
                });
            }
        }

        return new Result(orders, inquiries);
    }

    // 열이 int unsigned라서 GetInt32 대신 변환
    private static int Int(MySqlDataReader r, int i) => Convert.ToInt32(r.GetValue(i));

    private static string? Opt(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
