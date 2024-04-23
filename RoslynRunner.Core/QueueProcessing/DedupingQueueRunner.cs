namespace RoslynRunner.Core.QueueProcessing;

public static class DedupingQueueRunner
{
    
    /// <summary>
    /// A convenience function for avoiding recursion and avoids reprocessing items
    /// </summary>
    /// <param name="processor">A function which runs on an individual item and returns additional items to examine</param>
    /// <param name="initial">The starting set of items to examine</param>
    /// <typeparam name="T">The type of item</typeparam>
    /// <returns>All items that were processed</returns>
    public static Task<IEnumerable<T>> ProcessResultsAsync<T>(Func<T, Task<IEnumerable<T>>> processor, params T[] initial) => ProcessResultsAsync(
        processor: processor,
        maxLength: 1000*1000,
        initial: initial
        );
    
    /// <summary>
    /// A convenience function for avoiding recursion and avoids reprocessing items
    /// </summary>
    /// <param name="processor">A function which runs on an individual item and returns additional items to examine</param>
    /// <param name="initial">The starting set of items to examine</param>
    /// <param name="maxLength">The maximum number of queue items before aborting</param>
    /// <typeparam name="T">The type of item</typeparam>
    /// <returns>All items that were processed</returns>
    public static async Task<IEnumerable<T>> ProcessResultsAsync<T>(Func<T, Task<IEnumerable<T>>> processor, long maxLength, params T[] initial)
    {
        Queue<T> queue = new Queue<T>(initial);
        HashSet<T> processed = new HashSet<T>(initial);
        while(queue.Count != 0)
        {
            if (maxLength > 0 && queue.Count > maxLength)
            {
                throw new Exception("queue limit exceeded");
            }
            T current = queue.Dequeue();
            IEnumerable<T> newNodes = await processor(current);
            foreach(T newItem in newNodes)
            {
                if(processed.Add(newItem))
                {
                    queue.Enqueue(newItem);
                }
            }
        }
        return processed;
    }

    public static IEnumerable<T> ProcessResults<T>(Func<T, IEnumerable<T>> processor, params T[] initial)
    {
        Queue<T> queue = new Queue<T>(initial);
        HashSet<T> processed = new HashSet<T>(initial);

        while (queue.Count != 0)
        {
            T current = queue.Dequeue();
            IEnumerable<T> newNodes = processor(current);
            foreach (T newItem in newNodes)
            {
                if (processed.Add(newItem))
                {
                    queue.Enqueue(newItem);
                }
            }
        }
        return processed;
    }
}