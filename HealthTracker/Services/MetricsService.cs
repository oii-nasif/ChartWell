using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;

namespace HealthTracker.Services;

public class MetricsService
{
    private readonly HealthMetricRepository _repo;

    public MetricsService(HealthMetricRepository repo)
    {
        _repo = repo;
    }

    public async Task<HealthMetric> AddMetric(string type, double value, double? secondValue,
        string unit, string recordedAt, string? notes)
    {
        // RULE-04: validate range before saving
        if (!ValidationHelper.IsMetricValueValid(type, value, secondValue, out var error))
            throw new ArgumentException(error);

        var metric = new HealthMetric
        {
            MetricType = type,
            Value = value,
            SecondValue = secondValue,
            Unit = unit,
            RecordedAt = recordedAt,
            Notes = notes
        };

        await _repo.InsertAsync(metric);
        return metric;
    }

    public Task<HealthMetric?> GetLatestByType(string type) =>
        _repo.GetLatestByTypeAsync(type);

    public Task<List<HealthMetric>> GetHistoryByType(string type, int count = 30) =>
        _repo.GetHistoryByTypeAsync(type, count);

    public async Task UpdateMetric(HealthMetric metric)
    {
        if (!ValidationHelper.IsMetricValueValid(metric.MetricType, metric.Value, metric.SecondValue, out var error))
            throw new ArgumentException(error);
        await _repo.UpdateAsync(metric);
    }

    public Task DeleteMetric(HealthMetric metric) => _repo.DeleteAsync(metric);

    public async Task<Dictionary<string, HealthMetric?>> GetAllLatest()
    {
        var result = new Dictionary<string, HealthMetric?>();
        foreach (var type in MetricTypes.All)
            result[type] = await _repo.GetLatestByTypeAsync(type);
        return result;
    }
}
