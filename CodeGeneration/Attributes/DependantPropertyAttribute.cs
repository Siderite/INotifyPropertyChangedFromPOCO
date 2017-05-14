using System;

namespace CodeGeneration
{
  public class DependantPropertyAttribute:Attribute
  {
    public DependantPropertyAttribute(string propertyName)
    {
      PropertyName = propertyName;
    }

    public string PropertyName { get; set; }
  }
}