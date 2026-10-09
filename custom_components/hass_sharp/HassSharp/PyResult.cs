using Python.Runtime;

namespace HassSharp;

// TODO: Rethink this, object[] is kinda bad
public readonly struct PyResult
{
    readonly object[]? _errors;
    readonly object? _result;

    PyResult(object? error)
    {
        if (error != null)
            _errors = [error];
        _result = error is null;
    }

    PyResult(object[] errors)
    {
        _errors = errors;
        _result = false;
    }

    PyResult(object? error, object? result)
    {
        if (error != null)
            _errors = [error];
        _result = result;
    }


    public static PyResult Success()
    {
        return new((object?)null);
    }

    public static PyResult Success(object result)
    {
        return new(null, result);
    }

    public static PyResult Error(object error)
    {
        return new(error);
    }

    public static PyResult Error(object error, object? result)
    {
        return new(error, result);
    }

    public static PyResult Errors(object[] errors)
    {
        return new(errors);
    }

    public static PyResult Errors(string[] errors)
    {
        return new(errors.Select(object (e) => e).ToArray());
    }


    public static implicit operator PyTuple(PyResult res) => res.ToPy();

    PyTuple ToPy()
    {
        using var _ = Py.GIL();

        var error = PyObject.None;

        if (_errors != null)
            error = _errors.Length == 1 ? _errors[0].ToPython() : _errors.EnumerableToPy();

        var result = _result is null ? PyObject.None : _result.ToPython();
        return new([result, error]);
    }
}
