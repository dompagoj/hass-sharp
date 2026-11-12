using Python.Runtime;

namespace HassSharp;

static class PyDTOConverter
{
    public static PyList EnumerableToPy<T>(this IEnumerable<T> e)
    {
        var list = new PyList();
        foreach (var elem in e)
        {
            if (elem is PyObject pyObj) list.Append(pyObj);
            else list.Append(elem.ToPythonAs());
        }

        return list;
    }

    static PyDict ClrObjToPy(this object? obj) => ClrObjToImpl(obj, obj?.GetType());
    static PyDict ClrObjToPy<T>(this T? obj) => ClrObjToImpl(obj, typeof(T));


    static PyDict ClrObjToImpl(this object? obj, Type? type)
    {
        var dict = new PyDict();
        if (obj == null || type == null) return dict;

        foreach (var prop in type.GetProperties())
        {
            var val = prop.GetValue(obj);
            if (val is IEnumerable<object> enumerable) dict[prop.Name] = enumerable.EnumerableToPy();
            else dict[prop.Name] = val.ToPython();
        }

        return dict;
    }


    public static PyList UserScriptToDto(List<UserScriptDTO> userScripts)
    {
        using var _ = Py.GIL();
        var pyList = new PyList();
        foreach (var script in userScripts)
        {
            var dict = new PyDict();
            dict["automationId"] = (script.AutomationId == null ? PyObject.None : new PyString(script.AutomationId));
            dict["revision"] = new PyString(script.Revision);
            dict["active"] = script.Active.ToPython();
            dict["activationError"] = script.ActivationError == null ? PyObject.None : new PyString(script.ActivationError);
            dict["fileName"] = new PyString(script.FileName);
            dict["classes"] = script.Classes.Select(g =>
            {
                var classDict = new PyDict();
                classDict["name"] = new PyString(g.Name);
                classDict["methods"] = g.Methods.EnumerableToPy();
                return classDict;
            }).EnumerableToPy();


            pyList.Append(dict);
        }

        return pyList;
    }
}
