namespace HassSharp;

public static class DictionaryExt
{
    extension<T, TVal>(Dictionary<T, TVal> dict) where T : notnull
    {
        public IEnumerable<(T, TVal)> PopAll()
        {
            foreach (var key in dict.Keys)
            {
                if (dict.Remove(key, out var val)) yield return (key, val);
            }
        }
    }
}
