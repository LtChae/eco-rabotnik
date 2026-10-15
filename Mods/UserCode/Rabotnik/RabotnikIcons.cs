// Rabotnik - icon carrier items. The client only registers a bundle icon (an ItemTemplate in rabotnik.unity3d) when the
// server has an item of the same name: a template with no matching item logged "Cannot find icon with name" in game.
// So each non-item icon (component tabs, Ecopedia pages) gets a hidden item that is never given or crafted, like vanilla's
// hidden body-part items. Built by Client/Assets/Rabotnik/Editor/LogbookBuilder.cs.
namespace Eco.Mods.TechTree
{
    using System.ComponentModel;
    using Eco.Core.Items;
    using Eco.Gameplay.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Serialization;

    /// <summary>The Rabotnik mask ("Android mask" by Lorc, CC BY 4.0): the Logbook's Rabotnik tab and the Rabotnik Ecopedia pages.</summary>
    [Serialized, LocDisplayName("Rabotnik"), LocDescription("The Rabotnik icon."), Category("Hidden"), Tag("NotInBrowser")]
    public partial class RabotnikMaskItem : Item { }
}
