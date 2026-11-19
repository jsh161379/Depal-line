using BareunOrder.Core.Models;
using MySqlConnector;

namespace BareunOrder.Feature.Stats;

/// <summary>
/// 통계 탭: 고른 기간의 주문을 MySQL에서 읽는다 (읽기 전용 계정 ui_reader, settings의 MySqlConnection).
/// - 저장은 DB 저장 프로그램(mqtt_db_writer.py)이 한다. 여기는 읽기만.
/// - 통계에 필요 없는 주소·메모는 읽지 않는다.
/// - 표 이름·열 이름은 DB_변경_2026-11-17_주문.sql 기준 (Core의 OrderDbReader와 같음)
/// </summary>
internal static class StatsDbReader
{
    /// <summary>received_at이 from 이상, to 미만인 주문 (취소 포함)</summary>
    public static async Task<List<Order>> LoadAsync(string connectionString, DateTime from, DateTime to, CancellationToken ct)
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
                "WHERE o.received_at >= @from AND o.received_at < @to ORDER BY i.order_item_id";
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var no = r.GetString(0);
                if (!items.TryGetValue(no, out var list)) items[no] = list = new List<OrderItem>();
                list.Add(new OrderItem(r.GetString(1), r.GetString(2), Int(r, 3), Int(r, 4)));
            }
        }

        // ② 주문
        var orders = new List<Order>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT order_no, received_at, customer_name, customer_phone, total_amount, order_status, courier, tracking_no " +
                "FROM orders WHERE received_at >= @from AND received_at < @to ORDER BY received_at";
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var no = r.GetString(0);
                orders.Add(new Order
                {
                    No = no,
                    ReceivedAt = r.GetDateTime(1),
                    Name = r.GetString(2),
                    Phone = r.GetString(3),
                    Total = Int(r, 4),
                    Status = OrderStatusText.FromWire(r.GetString(5)),
                    Courier = r.IsDBNull(6) ? null : r.GetString(6),
                    Tracking = r.IsDBNull(7) ? null : r.GetString(7),
                    Items = items.TryGetValue(no, out var list) ? list : Array.Empty<OrderItem>(),
                });
            }
        }
        return orders;
    }

    // 열이 int unsigned라서 GetInt32 대신 변환
    private static int Int(MySqlDataReader r, int i) => Convert.ToInt32(r.GetValue(i));
}
