using System.Collections.ObjectModel;

namespace CodexUsage.Application;

public readonly record struct DashboardCollectionSynchronizationResult(bool HasStructuralChanges);

public static class DashboardCollectionReconciler
{
    public static bool WouldRequireStructuralChanges<TItem, TKey>(
        IReadOnlyList<TItem> current,
        IReadOnlyList<TItem> replacement,
        Func<TItem, TKey> keySelector)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(keySelector);

        if (current.Count != replacement.Count) return true;

        var comparer = EqualityComparer<TKey>.Default;
        for (var index = 0; index < current.Count; index++)
        {
            if (!comparer.Equals(keySelector(current[index]), keySelector(replacement[index])))
                return true;
        }

        return false;
    }

    public static DashboardCollectionSynchronizationResult Synchronize<TItem, TKey>(
        ObservableCollection<TItem> target,
        IEnumerable<TItem> replacement,
        Func<TItem, TKey> keySelector,
        Action<TItem, TItem> update)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(update);

        var hasStructuralChanges = false;
        var desired = replacement as IReadOnlyList<TItem> ?? replacement.ToArray();
        var comparer = EqualityComparer<TKey>.Default;
        for (var index = 0; index < desired.Count; index++)
        {
            var incoming = desired[index];
            if (index < target.Count
                && comparer.Equals(keySelector(target[index]), keySelector(incoming)))
            {
                update(target[index], incoming);
                continue;
            }

            var existingIndex = FindExistingIndex(target, incoming, index + 1, keySelector, comparer);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
                hasStructuralChanges = true;
                update(target[index], incoming);
                continue;
            }

            target.Insert(index, incoming);
            hasStructuralChanges = true;
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
            hasStructuralChanges = true;
        }

        return new DashboardCollectionSynchronizationResult(hasStructuralChanges);
    }

    private static int FindExistingIndex<TItem, TKey>(
        IReadOnlyList<TItem> target,
        TItem incoming,
        int startIndex,
        Func<TItem, TKey> keySelector,
        EqualityComparer<TKey> comparer)
        where TKey : notnull
    {
        var incomingKey = keySelector(incoming);
        for (var index = startIndex; index < target.Count; index++)
        {
            if (comparer.Equals(keySelector(target[index]), incomingKey)) return index;
        }

        return -1;
    }
}
