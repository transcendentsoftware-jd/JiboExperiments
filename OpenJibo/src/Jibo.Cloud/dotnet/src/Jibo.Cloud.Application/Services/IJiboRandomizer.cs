namespace Jibo.Cloud.Application.Services;

public interface IJiboRandomizer
{
    T Choose<T>(IReadOnlyList<T> items);
}

public sealed class DefaultJiboRandomizer : IJiboRandomizer
{
    public T Choose<T>(IReadOnlyList<T> items)
    {
        return items.Count == 0
            ? throw new InvalidOperationException("Cannot choose from an empty list.")
            : items[Random.Shared.Next(items.Count)];
    }
}
