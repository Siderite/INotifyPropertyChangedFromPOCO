using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeGeneration
{
  public static class TypeFactory
  {
    private static readonly Dictionary<Type, Type> sCachedTypes = new Dictionary<Type, Type>();
    private static readonly Dictionary<string, string> sCachedTemplates = new Dictionary<string, string>();

    private static readonly Regex sDependenciesRegex =
      new Regex(@"\{dependencies(?:\s+(?<propertyChangedMethodName>[^\}]+))?\}",
                RegexOptions.Compiled | RegexOptions.Singleline);


    /// <summary>
    /// true if a read/write property is overridable
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    internal static bool ShouldBeProxied(this PropertyInfo info)
    {
      if (info == null)
        return false;
      // special attribute DoNotProxyPropertyAttribute can 
      // be used to block the proxying of a property
      if (info.GetCustomAttributes(typeof(DoNotProxyPropertyAttribute), false).Length > 0)
        return false;
      var getMethod = info.GetGetMethod();
      var setMethod = info.GetSetMethod();
      return (info.CanRead && getMethod.IsVirtual && !getMethod.IsFinal)
             &&
             (info.CanWrite && setMethod.IsVirtual && !setMethod.IsFinal);
    }

    /// <summary>
    /// Get an instance of an INotifyPropertyChanged proxy of a 
    /// type with optional constructor parameters.
    /// Use it instead of new T(arguments)
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="arguments"></param>
    /// <returns></returns>
    public static T GetINotifyPropertyChangedInstance<T>(params object[] arguments)
    {
      Type type = GetINotifyPropertyChangedType<T>();
      return (T)Activator.CreateInstance(type, arguments);
    }

    /// <summary>
    /// Get an INotifyPropertyChanged proxy of a type 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Type GetINotifyPropertyChangedType<T>()
    {
      return GetINotifyPropertyChangedType(typeof(T));
    }

    /// <summary>
    /// Get an INotifyPropertyChanged proxy of a type 
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static Type GetINotifyPropertyChangedType(Type type)
    {
      return GetINotifyPropertyChangedTypes(type).First();
    }

    /// <summary>
    /// Get INotifyPropertyChanged proxies of 
    /// an array of types
    /// </summary>
    /// <param name="types"></param>
    /// <returns></returns>
    public static Type[] GetINotifyPropertyChangedTypes(params Type[] types)
    {
      lock (((ICollection)sCachedTypes).SyncRoot)
      {
        // check the types are unique and not already cached
        var typeArray = types.Distinct()
          .Where(type => !sCachedTypes.ContainsKey(type))
          .ToArray();
        // only compile assembly if there are types to compile
        if (typeArray.Length > 0)
        {
          // generate proxy assembly
          var sourceCode = getINotifyPropertyChangedSourceCode(typeArray);
          var assembly = generateAssemblyFromCode(sourceCode);
          var assemblyTypes = assembly.GetTypes();
          // cache the proxies
          for (var i = 0; i < typeArray.Length; i++)
          {
            var type = typeArray[i];
            sCachedTypes[type] = assemblyTypes[i];
          }
        }
        // return a list of cached proxies depending on the requested types
        return types.Select(type => sCachedTypes[type]).ToArray();
      }
    }

    private static string getINotifyPropertyChangedSourceCode(params Type[] types)
    {
      // create using statements from INotifyPropertyChanges, 
      // the proxied types and the return types of the overriden properties
      // as well as the types of the parameterers in the public constructors
      var namespaces = new List<string>
                       {
                         "System.ComponentModel"
                       };
      var propertyDictionary = new Dictionary<Type, IEnumerable<PropertyInfo>>();
      var constructorDictionary = new Dictionary<Type, IEnumerable<ConstructorInfo>>();
      foreach (var type in types)
      {
        var properties = type.GetProperties().Where(p => p.ShouldBeProxied());
        var constructors = type.GetConstructors();
        // cache the properties and the constructors, we are going to need them later
        propertyDictionary[type] = properties;
        constructorDictionary[type] = constructors;
        namespaces.AddRange(getNamespaces(type));
        foreach (var propertyInfo in properties)
        {
          namespaces.AddRange(getNamespaces(propertyInfo.PropertyType));
          //if the property is indexed, get the namespaces of types of the index parameters as well
          namespaces.AddRange(
              propertyInfo.GetIndexParameters().SelectMany(
                  indexParameter => getNamespaces(indexParameter.ParameterType)));
        }
        foreach (var constructorInfo in constructors)
        {
          namespaces.AddRange(constructorInfo.GetParameters().SelectMany(parameterInfo => getNamespaces(parameterInfo.ParameterType)));
        }
      }
      var sourceBuilder = new StringBuilder();
      foreach (var ns in namespaces.Distinct())
      {
        sourceBuilder.AppendFormat("using {0};\r\n", ns);
      }

      // add a namespace (not necessary, but a good practice 
      // as otherwise the generated classes have a null namespace)
      sourceBuilder.AppendLine("namespace __autoGenerated {");
      // template for generating a class
      var classTemplate = getTemplate("INotifyPropertyChangedClassTemplate.txt");
      // template for the notifying property in the generated class
      var propertyTemplate = getTemplate("INotifyPropertyChangedPropertyTemplate.txt");
      foreach (var type in types)
      {
        var properties = propertyDictionary[type];
        // give the class a unique internal name
        var className = "@autonotify_" + type.Name;
        // generate the properties code
        var propertiesBuilder = new StringBuilder();
        foreach (PropertyInfo propertyInfo in properties)
        {
          string propertyString = propertyTemplate
            .Replace("{propertyType}", propertyInfo.PropertyType.FullName)
            .Replace("{propertyName}", propertyInfo.Name);
          propertyString = replaceDependencies(propertyString, propertyInfo);
          propertiesBuilder.AppendLine(propertyString);
        }
        // generate the code for public constructors
        var constructorsBuilder = new StringBuilder();
        var constructors = constructorDictionary[type];
        foreach (var constructorInfo in constructors)
        {
          var sb1 = new StringBuilder();
          var sb2 = new StringBuilder();
          foreach (var parameterInfo in constructorInfo.GetParameters())
          {
            if (sb1.Length > 0)
            {
              sb1.Append(", ");
            }
            if (sb2.Length > 0)
            {
              sb2.Append(", ");
            }
            sb1.Append(parameterInfo.ParameterType.FullName + " " + parameterInfo.Name);
            sb2.Append(parameterInfo.Name);
          }
          constructorsBuilder.AppendFormat("public {0}({1}):base({2}){{}}\r\n", className, sb1, sb2);
        }

        // generate the class code
        string sourceCode = classTemplate
            .Replace("{className}", className)
            .Replace("{baseClassName}", type.Name)
            .Replace("{constructors}", constructorsBuilder.ToString())
            .Replace("{properties}", propertiesBuilder.ToString());
        sourceBuilder.AppendLine(sourceCode);
      }
      // end namespace block
      sourceBuilder.AppendLine("}");
#if DEBUG
      Debug.WriteLine(sourceBuilder);
#endif
      return sourceBuilder.ToString();
    }


    /// <summary>
    /// replace the special token {dependencies MethodName} with the property 
    /// change event fire for each dependant property
    /// </summary>
    /// <param name="propertyString"></param>
    /// <param name="propertyInfo"></param>
    /// <returns></returns>
    private static string replaceDependencies(string propertyString, PropertyInfo propertyInfo)
    {
      var m = sDependenciesRegex.Match(propertyString);
      if (!m.Success)
        return propertyString;
      var propertyChangedMethodName = m.Groups["propertyChangedMethodName"].Value;
      if (string.IsNullOrWhiteSpace(propertyChangedMethodName))
        propertyChangedMethodName = "OnPropertyChanged";
      var dependenciesBuilder = new StringBuilder();
      var dependantProperties = propertyInfo.GetCustomAttributes(typeof(DependantPropertyAttribute), true);
      foreach (DependantPropertyAttribute attribute in dependantProperties)
      {
        dependenciesBuilder.AppendFormat("{0}(\"{1}\");\r\n", propertyChangedMethodName, attribute.PropertyName);
      }
      return propertyString.Substring(0, m.Index) + dependenciesBuilder + propertyString.Substring(m.Index + m.Length);
    }

    private static string getTemplate(string resourceName)
    {
      lock (((ICollection)sCachedTemplates).SyncRoot)
      {
        string template;
        // if cached, just return the template
        if (sCachedTemplates.TryGetValue(resourceName, out template))
          return template;
        // get a text template from an embedded resource
        var templateAssembly = Assembly.GetAssembly(typeof(TypeFactory));
        // I am too lazy to think of what namespace each template has
        // Get the full name by filename only
        resourceName = templateAssembly.GetManifestResourceNames()
          .First(name => name == resourceName || name.EndsWith("." + resourceName));
        // cache the content of the template and return it
        using (Stream stream = templateAssembly.GetManifestResourceStream(resourceName))
        {
          if (stream == null)
            return null;
          using (var streamReader = new StreamReader(stream))
          {
            template = streamReader.ReadToEnd();
            sCachedTemplates[resourceName] = template;
            return template;
          }
        }
      }
    }

    private static Assembly generateAssemblyFromCode(string sourceCode)
    {
      // CSharp code provider.
      var codeProvider = CodeDomProvider.CreateProvider("CSharp");
      var parameters = new CompilerParameters
                         {
                           GenerateExecutable = false,
                           GenerateInMemory = true
                         };
      // get the locations of loaded assemblies in the domain
      var locations = AppDomain.CurrentDomain.GetAssemblies()
        .Where(v => !v.IsDynamic).Select(a => a.Location).ToArray();
      // and reference them in the compiled assembly
      parameters.ReferencedAssemblies.AddRange(locations);
      CompilerResults results = codeProvider.CompileAssemblyFromSource(parameters, sourceCode);
      // One can choose to throw an error here
#if DEBUG
      foreach (CompilerError error in results.Errors)
      {
        Debug.WriteLine("Error: " + error.ErrorText);
      }
#endif
      return results.Errors.Count > 0
               ? null
               : results.CompiledAssembly;
    }

    /// <summary>
    /// Find all namespace related to a type, including generic argument types
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    private static IEnumerable<string> getNamespaces(Type type)
    {
      if (type == null) yield break;
      yield return type.Namespace;
      foreach (var ns in type.GetGenericArguments().SelectMany(getNamespaces))
      {
        yield return ns;
      }
    }
  }
}