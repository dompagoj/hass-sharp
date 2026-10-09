namespace HassSharp;

public static class Result
{
    public static Ok<T> Ok<T>(T value) => new(value);

    public static Err<E> Err<E>(E value) => new(value);
}

public readonly record struct Ok<T>(T Value)
{
    public static implicit operator T(Ok<T> res) => res.Value;

    public static Ok<T> New(T value) => new(value);
};

public readonly record struct Err<E>(E Value)
{
    public static implicit operator E(Err<E> res) => res.Value;

    public static Err<E> New(E value) => new(value);
}

public union Result<T, E>(Ok<T>, Err<E>)
{
    public static implicit operator bool(Result<T, E> res) => res is Ok<T>;

    public static implicit operator PyResult(Result<T, E> res)
    {
        return res.Value switch
        {
            Ok<T> ok => PyResult.Success(ok),
            Err<E> err => PyResult.Error(err),
            _ => throw new(),
        };
    }

    public bool IsErr() => this is Err<E>;
    public bool IsOk() => this is Ok<T>;

    public bool IsOk(out T val)
    {
        var isOk = IsOk();
        if (isOk) val = (Ok<T>)this.Value!;

        val = default!;

        return IsOk();
    }

    public Result<TMapped, E> MapOk<TMapped>(Func<T, TMapped> cb)
    {
        if (IsErr()) return Result.Err(this.Err());

        return Result.Ok(cb(this.Ok()));
    }

    public Result<T, EMapped> MapError<EMapped>(Func<E, EMapped> cb)
    {
        if (IsOk()) return Result.Ok(this.Ok());

        return Result.Err(cb(this.Err()));
    }


    public E Err()
    {
        if (this.Value is Err<E> err) return err.Value;

        throw new("Not an error");
    }

    public T Ok()
    {
        if (this.Value is Ok<T> ok) return ok.Value;

        throw new("Not an ok");
    }
}

public union Result<T>(Ok<T>, Err<Exception>)
{
    public static implicit operator bool(Result<T> res) => res is Ok<T>;

    public static PyResult ToPyResult(Result<T> res)
    {
        return res.Value switch
        {
            Ok<T> ok => PyResult.Success(ok),
            Err<Exception> ex => PyResult.Error(ex.Value.Message),
            _ => throw new(),
        };
    }

    public bool IsErr() => this is Err<Exception>;
    public bool IsOk() => this is Ok<T>;

    public bool IsOk(out T val)
    {
        var isOk = IsOk();
        if (isOk) val = (Ok<T>)this.Value!;

        val = default!;

        return IsOk();
    }

    public Result<TMapped, Exception> MapOk<TMapped>(Func<T, TMapped> cb)
    {
        if (IsErr()) return Result.Err(this.Err());

        return Result.Ok(cb(this.Ok()));
    }

    public Result<T, EMapped> MapError<EMapped>(Func<Exception, EMapped> cb)
    {
        if (IsOk()) return Result.Ok(this.Ok());

        return Result.Err(cb(this.Err()));
    }


    public Exception Err()
    {
        if (this.Value is Err<Exception> err) return err.Value;

        throw new("Not an error");
    }

    public T Ok()
    {
        if (this.Value is Ok<T> ok) return ok.Value;

        throw new("Not an ok");
    }
}
