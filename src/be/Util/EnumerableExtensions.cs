namespace Util;

// TODO: Check all the C# code in this repo where the utility methods in this class can be used

public static class EnumerableExtensions
{
    public static bool NotAny<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        return !source.Any(predicate);
    }
}