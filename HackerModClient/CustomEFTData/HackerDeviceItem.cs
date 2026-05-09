using EFT.InventoryLogic;
using WTTClientCommonLib.Attributes;

namespace Manimal.HackerMod.CustomEFTData
{
    // marker class. the [CustomParent] binds custom parent guid 48048b8a... to this
    // type — items with that parentId instantiate as HackerDeviceItem instead of
    // the default KeyItemClass.
    //
    // inherits KeyItemClass so the engine allocates a KeyComponent at item-instantiate
    // time. that gives us free NumberOfUsages tracking + auto-discard at MaximumNumberOfUsage.
    // server-side custom parent JSON correspondingly derives from KeyMechanical (5c99f98d...).
    //
    // having a dedicated type lets controller-routing patches detect "is this our device?"
    // via `item is HackerDeviceItem` instead of comparing template ids.
    [CustomParent("48048b8a031d1c9aac192451", typeof(HackerDeviceItem), typeof(KeyTemplateClass))]
    public sealed class HackerDeviceItem : KeyItemClass
    {
        public HackerDeviceItem(string id, KeyTemplateClass template) : base(id, template) { }
    }
}
