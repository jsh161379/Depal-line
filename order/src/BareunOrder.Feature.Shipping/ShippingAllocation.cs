using BareunOrder.Core.Data;

namespace BareunOrder.Feature.Shipping;

/// <summary>
/// 출고한 몫을 이 프로그램 안에서 즉시 예약한다. DB 반영을 기다리는 동안 같은 수량을 재사용하지 않는다.
/// 여러 PC 사이의 동시 예약은 서버의 원자적 처리 없이는 보장할 수 없다.
/// </summary>
public sealed class ShippingAllocation
{
    private readonly Dictionary<string, int> _linkedFloor = new(StringComparer.Ordinal);

    public int Available(IReadOnlyList<LineRun> runs) => runs.Sum(Free);

    private int Free(LineRun run) => Math.Max(0,
        run.ACount - Math.Max(run.Linked, _linkedFloor.GetValueOrDefault(run.RunId)));

    public IReadOnlyList<RunTake>? Plan(IReadOnlyList<LineRun> runs, int needed)
    {
        if (needed <= 0 || needed > Available(runs)) return null;
        var left = needed;
        var result = new List<RunTake>();
        foreach (var run in runs.OrderBy(r => r.StartedAt))
        {
            var count = Math.Min(left, Free(run));
            if (count == 0) continue;
            result.Add(new RunTake(run.RunId, count));
            left -= count;
            if (left == 0) break;
        }
        return left == 0 ? result : null;
    }

    public void Reserve(IReadOnlyList<LineRun> runs, IReadOnlyList<RunTake> takes)
    {
        foreach (var take in takes)
        {
            var run = runs.Single(r => r.RunId == take.RunId);
            if (take.Count <= 0 || take.Count > Free(run))
                throw new InvalidOperationException("회차별 출고 가능 수량이 변경됐습니다. 다시 확인해 주세요.");
        }
        foreach (var take in takes)
        {
            var run = runs.Single(r => r.RunId == take.RunId);
            _linkedFloor[take.RunId] = Math.Max(run.Linked,
                _linkedFloor.GetValueOrDefault(take.RunId)) + take.Count;
        }
    }
}
