using Menu.Remix.MixedUI;
using UnityEngine;

namespace OptimizedRemix;
internal class Options : OptionInterface
{
    public static readonly Options Instance = new Options();

    public static Configurable<bool> LoadThumbnails = Instance.config.Bind("LoadThumbnails", true);
    public static Configurable<bool> ResizeLocalThumbnails = Instance.config.Bind("OptimizeLocalThumbnails", false);

    public override void Initialize()
    {
        OpTab tab = new(this, "Options");
        Tabs = [tab];

        float y = 400f;
        UIelement[] elements = [
            new OpLabel(new Vector2(300, 550), default, "Optimized Remix Menu", FLabelAlignment.Center, true),
            new OpCheckBox(LoadThumbnails, new Vector2(100f, y)) { description = "Disabling mod thumbnails will reduce ram usage and stuttering." },
            new OpLabel(140f, y+2, "Mod thumbnails"),
            new OpCheckBox(ResizeLocalThumbnails, new Vector2(100f, y -= 50)) { description = "Resizes local mod thumbnails during startup. This is reduces thumbnail load time, but causes problems if you develop mods." },
            new OpLabel(140f, y+2, "Resize local mod thumbnails"),
        ];
        tab.AddItems(elements);
    }
}
