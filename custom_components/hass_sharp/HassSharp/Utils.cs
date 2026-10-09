namespace HassSharp;

static class Utils
{
    public static PyResult WrapPyResult(Action cb)
    {
        try
        {
            cb();
            return PyResult.Success();
        }
        catch (Exception ex)
        {
            return PyResult.Error(ex.Message);
        }
    }

    public static async Task<PyResult> WrapPyResult(Func<Task> cb)
    {
        try
        {
            await cb();
            return PyResult.Success();
        }
        catch (Exception ex)
        {
            return PyResult.Error(ex.Message);
        }
    }

    public static PyResult WrapPyResult<T>(Func<T> cb)
        where T : notnull
    {
        try
        {
            var res = cb();
            return PyResult.Success(res);
        }
        catch (Exception ex)
        {
            return PyResult.Error(ex.Message);
        }
    }

    public static async Task<PyResult> WrapPyResult<T>(Func<Task<T>> cb)
        where T : notnull
    {
        try
        {
            var res = await cb();
            return PyResult.Success(res);
        }
        catch (Exception ex)
        {
            return PyResult.Error(ex.Message);
        }
    }
}
