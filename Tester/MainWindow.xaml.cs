using System.ComponentModel;
using System.Windows;
using CodeGeneration;

namespace Tester
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window
  {
    public MainWindow()
    {
      InitializeComponent();
      Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
      // ridiculous demo

      // cache the proxies for multiple types
      // types can be repeated or just plain wrong. The only issue would be with sealed types.
      TypeFactory.GetINotifyPropertyChangedTypes(typeof(Poco),typeof(MainWindow), typeof(Poco));
      TypeFactory.GetINotifyPropertyChangedTypes(typeof(Poco), typeof(MainWindow), typeof(Poco));
      // create instance of a Poco proxy
      var notifyPoco = TypeFactory.GetINotifyPropertyChangedInstance<Poco>();
      // alert any change in the properties
      ((INotifyPropertyChanged) notifyPoco).PropertyChanged +=
        (sndr, args) => MessageBox.Show(string.Format("Changed! ({0})", args.PropertyName));
      // change the Value property
      // an alert should have appeared on the screen for Value,
      // then for the dependant property DependantOnValue
      notifyPoco.Value = "Siderite!";
      // change the ShouldNotBeProxied property
      // nothing should happen as it has been marked with DoNotProxyProperty
      notifyPoco.ShouldNotBeProxied = "If you see this, something went wrong!";
      // end test
      MessageBox.Show("End test");
    }
  }


  public class Poco
  {
    [DependantProperty("DependantOnValue")]
    public virtual string Value { get; set; }
    public virtual string DependantOnValue { get; set; }
    [DoNotProxyProperty]
    public virtual string ShouldNotBeProxied { get; set; }
  }
}