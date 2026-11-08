using System;
using FortnitePorting.Controls.Navigation.Sidebar;
using FortnitePorting.Framework;
using FortnitePorting.ViewModels;

namespace FortnitePorting.Views;

public partial class PluginView : ViewBase<PluginViewModel>
{
    public PluginView() : base(AppSettings.Plugin)
    {
        InitializeComponent();
        Navigation.Plugin.Initialize(Sidebar, ContentFrame);
        Navigation.Plugin.AddTypeResolver<EExportLocation>(location =>
        {
            var name = location.ToString();
            var viewName = $"FortnitePorting.Views.Plugin.{name}PluginView";
        
            var type = Type.GetType(viewName);
            return type;
        });
    }
    
    private void OnItemSelected(object? sender, SidebarItemSelectedArgs e)
    {
        switch (e.Tag)
        {
            case EExportLocation exportLocation:
                Navigation.Plugin.Open(exportLocation);
                break;
            case Type viewType:
                Navigation.Plugin.Open(viewType);
                break;
        }
    }
}