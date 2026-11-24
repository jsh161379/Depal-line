using System.Security.Cryptography;

namespace BareunOrder.Feature.Shipping;

/// <summary>연습 모드 전용 번호. 실제 택배 접수·송장 발급 API를 대체하지 않는다.</summary>
public static class DemoTrackingNumbers
{
    public static string Create(ISet<string> used)
    {
        for (var i = 0; i < 100; i++)
        {
            var value = "990" + RandomNumberGenerator.GetInt32(1_000_000_000).ToString("D9");
            if (used.Add(value)) return value;
        }
        throw new InvalidOperationException("연습 송장을 생성하지 못했습니다. 다시 시도해 주세요.");
    }
}
