using System.Text.Json;

namespace HassSharp;

/// <summary>
/// A store of values that outlives a recompile and a restart: JSON values and bytes, each under a key such as
/// <c>people/ivan</c>, kept on disk under <c>.storage/hass_sharp/data/</c>. Every write is on disk before it returns and
/// is never half-visible. A value takes at most 8 MiB and a store at most 512 MiB. Every operation is atomic against
/// every other handle on the same store; use <see cref="Update{T}"/> to change a value another device may change too.
/// A key is 1 to 200 characters of segments joined by <c>/</c>, each of lower-case letters, digits, <c>_</c>, <c>.</c> and
/// <c>-</c>, starting with a letter or a digit.
/// </summary>
public sealed class SharpData
{
    // This handle's own serialiser, never a shared one: the types it has seen, which may be a definition's, go with it.
    readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    internal SharpData(DataStores.Store store, DataUser user)
    {
        Store = store;
        User = user;
    }

    internal DataStores.Store Store { get; }
    internal DataUser User { get; }
    /// <summary>Set when its user is closed, under the store's lock.</summary>
    internal bool Closed { get; set; }

    /// <summary>The JSON value under a key, read as <typeparamref name="T"/>, as System.Text.Json reads it with its web defaults.</summary>
    /// <returns>The value; the type's default when the key has none.</returns>
    /// <exception cref="ArgumentException">The key is not one.</exception>
    /// <exception cref="InvalidOperationException">The key holds bytes.</exception>
    /// <exception cref="InvalidDataException">The value is damaged, or cannot be read as <typeparamref name="T"/>.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public T? Get<T>(string key)
    {
        DataStores.CheckKey(key);
        return Read<T>(key, Store.Read(this, key, DataStores.Json));
    }

    /// <summary>Writes a value under a key as JSON, as System.Text.Json writes it with its web defaults, replacing what the key held.</summary>
    /// <exception cref="ArgumentException">The key is not one.</exception>
    /// <exception cref="InvalidOperationException">The value takes more than 8 MiB, or would take the store past 512 MiB; nothing was written.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public void Set<T>(string key, T value)
    {
        DataStores.CheckKey(key);
        Store.Write(this, key, DataStores.Json, JsonSerializer.SerializeToUtf8Bytes(value, options));
    }

    /// <summary>The bytes under a key, such as a JPEG crop or an embedding.</summary>
    /// <returns>A copy of the bytes; null when the key has none.</returns>
    /// <exception cref="ArgumentException">The key is not one.</exception>
    /// <exception cref="InvalidOperationException">The key holds a JSON value.</exception>
    /// <exception cref="InvalidDataException">The value is damaged.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public byte[]? GetBytes(string key)
    {
        DataStores.CheckKey(key);
        return Store.Read(this, key, DataStores.Bytes)?.AsSpan(1).ToArray();
    }

    /// <summary>Writes bytes under a key, replacing what the key held.</summary>
    /// <exception cref="ArgumentException">The key is not one, or there are no bytes.</exception>
    /// <exception cref="InvalidOperationException">The bytes take more than 8 MiB, or would take the store past 512 MiB; nothing was written.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public void SetBytes(string key, byte[] value)
    {
        DataStores.CheckKey(key);
        if (value == null) throw new ArgumentException("Bytes to store cannot be null: remove the key instead");
        Store.Write(this, key, DataStores.Bytes, value);
    }

    /// <summary>Removes the value under a key.</summary>
    /// <returns>Whether the key had a value.</returns>
    /// <exception cref="ArgumentException">The key is not one.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public bool Remove(string key)
    {
        DataStores.CheckKey(key);
        return Store.Remove(this, key);
    }

    /// <summary>The keys that have a value and start with a prefix, such as <c>people/</c>, in ordinal order; every key without one.</summary>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public IReadOnlyList<string> Keys(string prefix = "") =>
        Store.Keys(this, prefix ?? throw new ArgumentException("A prefix of keys cannot be null: pass an empty one for every key"));

    /// <summary>
    /// Reads the JSON value under a key, changes it and writes what the change returns, as one step: no other handle on
    /// the store reads or writes in between, so two devices that update one key lose no update. Keep the change short;
    /// it holds the store.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="change">Given the value, the type's default when the key has none, returns the value to write.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentException">The key is not one.</exception>
    /// <exception cref="InvalidOperationException">The key holds bytes, or the new value takes more than 8 MiB or would take the store past 512 MiB; nothing was written.</exception>
    /// <exception cref="InvalidDataException">The value is damaged, or cannot be read as <typeparamref name="T"/>; nothing was written.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public T Update<T>(string key, Func<T?, T> change)
    {
        DataStores.CheckKey(key);
        if (change == null) throw new ArgumentException("An update needs a change to make");
        T written = default!;
        Store.Update(this, key, current =>
        {
            written = change(Read<T>(key, current));
            return JsonSerializer.SerializeToUtf8Bytes(written, options);
        });
        return written;
    }

    /// <summary>
    /// Runs an action after another handle on this store, another C# node's in this or another definition, wrote, removed
    /// or cleared: not after this handle's own. The action runs as a track callback does, one run at a time; changes
    /// while it runs cause one more run, not one each.
    /// </summary>
    /// <returns>Ends the subscription when disposed; it also ends when the definition stops.</returns>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public IDisposable Changed(Func<Task> action)
    {
        if (action == null) throw new ArgumentException("A subscription to changes needs an action to run");
        return Store.Subscribe(this, action);
    }

    /// <summary>Removes every value of the store.</summary>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public void Clear() => Store.Clear(this);

    // The JSON value of a stored file (its kind's byte, then the value) as T; the type's default when there is none.
    T? Read<T>(string key, byte[]? stored)
    {
        if (stored == null) return default;
        try { return JsonSerializer.Deserialize<T>(stored.AsSpan(1), options); }
        // The serialiser's message may quote the value, so it is not kept.
        catch (JsonException) { throw new InvalidDataException($"The stored value of '{key}' cannot be read as {typeof(T).Name}"); }
    }
}
