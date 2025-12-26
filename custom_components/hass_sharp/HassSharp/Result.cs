using Python.Runtime;

namespace HassSharp;

readonly struct PyResult
{
    readonly object[]? _errors;

    PyResult(object? error)
    {
        if (error != null)
            _errors = [error];
    }

    PyResult(object[] errors)
    {
        _errors = errors;
    }


    public static PyResult Success()
    {
        return new((object?)null);
    }

    public static PyResult Error(object error)
    {
        return new(error);
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

        return new([(_errors == null).ToPythonAs(), error]);
    }
}
