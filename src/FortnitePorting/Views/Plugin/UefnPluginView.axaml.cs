using FortnitePorting.Framework;
using FortnitePorting.ViewModels.Plugin;

namespace FortnitePorting.Views.Plugin;

public partial class UefnPluginView : ViewBase<UefnPluginViewModel>
{
    public UefnPluginView() : base(AppSettings.Plugin.Uefn)
    {
        InitializeComponent();
    }
}
