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


    public static PyList UserScriptToDto(HashSet<UserScript> userScripts)
    {
        using var _ = Py.GIL();
        var pyList = new PyList();
        foreach (var group in userScripts.GroupBy(s => s.CompiledUserScript.FileName))
        {
            var dict = new PyDict();
            dict["fileName"] = new PyString(group.Key);
            dict["classes"] = group.Select(g =>
            {
                var classDict = new PyDict();
                classDict["name"] = new PyString(g.ClassName);
                classDict["methods"] = g.Methods.Select(m => m.Name).EnumerableToPy();
                return classDict;
            }).EnumerableToPy();


            pyList.Append(dict);
        }

        return pyList;
    }
}
