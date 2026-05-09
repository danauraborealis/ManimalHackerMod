using UnityEngine;

namespace Manimal.HackerMod.CustomEFTData
{
    // stub returned by our patch on GClass2970.smethod_0 for our item type.
    // mirrors KomradeKids CustomUsableItemInterfaceClass.
    //
    // without an entry the dispatcher returns null which silently breaks the
    // controller-swap chain. this stub keeps subsequent code that expects a
    // non-null instance happy.
    public class HackerDeviceInterfaceClass : GInterface323
    {
        public void Initialize(GameObject gameObject) { }
        public void UpdateData(GStruct337 observedUsableItemUpdatedData) { }
        public void Disable() { }
    }
}
