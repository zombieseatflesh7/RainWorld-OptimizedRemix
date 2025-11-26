using BepInEx;
using Menu.Remix;
using Menu.Remix.MixedUI;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using UnityEngine;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace OptimizedRemix;

[BepInPlugin("zombieseatflesh7.OptimizedRemix", "Optimized Remix Menu", "1.1.3")]
public class Plugin : BaseUnityPlugin
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr LoadLibraryA(string lpLibFileName);

    public static new BepInEx.Logging.ManualLogSource Logger;
    private static bool initialized = false;
    private static FShader greyscaleShader;

    public void OnEnable()
    {
        Logger = base.Logger;
        On.RainWorld.OnModsInit += OnModsInit;
        On.RainWorld.PostModsInit += PostModsInit;
        Options.Instance.OnConfigChanged += OptimizeTextures;
    }

    // most of this code breaks if its run OnEnable for some reason lmao
    private void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);

        if (!initialized)
        {
            initialized = true;

            foreach (ModManager.Mod mod in ModManager.ActiveMods)
            {
                if (mod.id == "FasterRemix")
                {
                    throw new Exception($"Incompatible mod {mod.name}. You must disable this mod before you can use Optimized Remix Menu.");
                }
            }

            MachineConnector.SetRegisteredOI("OptimizedRemix", Options.Instance);

            // Shader loading
            AssetBundle assetBundle = AssetBundle.LoadFromFile(AssetManager.ResolveFilePath("optimizedremixbundle"));
            greyscaleShader = FShader.CreateShader("OptimizedRemix.Greyscale", assetBundle.LoadAsset<Shader>("Assets/Shaders/Greyscale.shader"));
            self.Shaders.Add("OptimizedRemix.Greyscale", greyscaleShader);

            // Hooks
            AddHooks();
        }
    }

    private void PostModsInit(On.RainWorld.orig_PostModsInit orig, RainWorld self)
    {
        orig(self);

        OptimizeTextures();
    }

    private bool hasOptimizedTextures = false;
    public void OptimizeTextures()
    {
        if (hasOptimizedTextures || !Options.LoadThumbnails.Value) return;

        try
        {
            hasOptimizedTextures = true;

            LoadLibraryA(AssetManager.ResolveFilePath("native/nvtt.dll"));
            LoadLibraryA(AssetManager.ResolveFilePath("native/FreeImage.dll"));

            List<string> modPNGList = new List<string>(ModManager.InstalledMods.Count);
            foreach (var m in ModManager.InstalledMods)
            {
                string modThumbnailPath = Path.Combine(m.path, "thumbnail.png");
                if (File.Exists(modThumbnailPath) && (m.workshopMod || Options.ResizeLocalThumbnails.Value))
                {
                    modPNGList.Add(modThumbnailPath);
                }
            }
            TextureResizer.Instance.FixThumbnailPNGSizes(modPNGList.ToArray());
        }
        catch (Exception e)
        {
            Logger.LogError("Error while resizing thumbnails");
            Logger.LogError(e);
        }
    }

    public static void AddHooks()
    {
        // Prevent these functions from running
        On.Menu.Remix.ConfigContainer.QueueModThumbnails += QueueModThumbnails;
        On.Menu.Remix.ConfigContainer._LoadModThumbnail += ConfigContainer_LoadModThumbnail;

        // optimize mod preview thumbnail
        IL.Menu.Remix.InternalOI_Stats.Initialize += InternalOI_Stats_Initialize_IL;
        IL.Menu.Remix.InternalOI_Stats._PreviewMod += PreviewMod_IL;

        // optimize mod buttons
        IL.Menu.Remix.MenuModList.ModButton.ctor += ModButton_Ctor_IL;
        On.Menu.Remix.MenuModList.ModButton._ProcessThumbnail += ModButton_ProcessThumbnail;
        On.Menu.Remix.MenuModList.ModButton._UpdateThumbnail += ModButton_UpdateThumbnail;
        IL.Menu.Remix.MenuModList.ModButton.GrafUpdate += ModButton_GrafUpdate_IL;
        IL.Menu.Remix.MenuModList.ModButton.UnloadUI += ModButton_UnloadUI_IL;
        
        // exception logging
        On.Menu.Remix.InternalOI_Stats.Initialize += (orig, self) =>
        {
            try { orig(self); }
            catch (Exception e) { Debug.LogException(e); }
        };
        On.Menu.Remix.InternalOI_Stats._PreviewMod += (orig, self, button) =>
        {
            try { orig(self, button); }
            catch (Exception e) { Debug.LogException(e); }
        };
        On.Menu.Remix.MenuModList.ModButton.ctor += (orig, self, list, index) =>
        {
            try { orig(self, list, index); }
            catch (Exception e) { Debug.LogException(e); }
        };
        On.Menu.Remix.MenuModList.ModButton.GrafUpdate += (orig, self, timeStacker) =>
        {
            try { orig(self, timeStacker); }
            catch (Exception e) { Debug.LogException(e); }
        };
        On.Menu.Remix.MenuModList.ModButton.UnloadUI += (orig, self) =>
        {
            try { orig(self); }
            catch (Exception e) { Debug.LogException(e); }
        };
    }

    // Removing these functions
    private static void QueueModThumbnails(On.Menu.Remix.ConfigContainer.orig_QueueModThumbnails orig, ConfigContainer self, MenuModList.ModButton[] buttons)
    { }

    private static int ConfigContainer_LoadModThumbnail(On.Menu.Remix.ConfigContainer.orig__LoadModThumbnail orig, ConfigContainer self, MenuModList.ModButton button)
    { return 1; }

    // changed OpImage to use atlas element instead of texture
    private static void InternalOI_Stats_Initialize_IL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        // Texture2D image = new Texture2D(1, 1, TextureFormat.ARGB32, mipChain: false);
        // imgThumbnail = new OpImage(default(Vector2), image);
        c.GotoNext(
            i => i.MatchLdloca(2),
            i => i.MatchInitobj<Vector2>(),
            i => i.MatchLdloc(2),
            i => i.MatchCall<Texture2D>("get_whiteTexture"),
            i => i.MatchNewobj<OpImage>(),
            i => i.MatchStfld<InternalOI_Stats>("imgThumbnail")
            );
        c.RemoveRange(6);
        
        // ldarg.0 already present
        c.EmitDelegate((InternalOI_Stats stats) =>
        {
            stats.imgThumbnail = new Menu.Remix.MixedUI.OpImage(default, ConfigContainer._GetThumbnailName(MenuModList.ModButton.RainWorldDummy.mod.id));
        });
    }

    // Fix to load and display unloaded thumbnails immediately
    private static void PreviewMod_IL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        c.GotoNext( // FAtlas atlasWithName = Futile.atlasManager.GetAtlasWithName(name);
            i => i.MatchLdsfld<Futile>("atlasManager"),
            i => i.MatchLdloc(1),
            i => i.MatchCallOrCallvirt<FAtlasManager>("GetAtlasWithName")
            );
        int index = c.Index; // IL_0048: ldsfld class FAtlasManager Futile::atlasManager

        c.GotoNext( // if (mod.DLCMissing)
            i => i.MatchLdloc(0),
            i => i.MatchCallOrCallvirt<ModManager.Mod>("get_DLCMissing"),
            i => i.Match(OpCodes.Brfalse_S)
            );
        ILLabel destination = c.MarkLabel(); // IL_00ea: ldloc.0
                                       
        c.Goto(index);
        c.Emit(OpCodes.Ldarg_0); // this
        c.Emit(OpCodes.Ldarg_1); // MenuModList.ModButton button
        c.EmitDelegate((InternalOI_Stats stats, MenuModList.ModButton button) =>
        {
            if (!Options.LoadThumbnails.Value)
            {
                stats.imgThumbnail.Hide();
                return;
            }

            string thumbnailName = ConfigContainer._GetThumbnailName(button.itf.mod.id);
            if (!button._thumbLoaded && !Futile.atlasManager.DoesContainAtlas(thumbnailName))
                LoadModThumbnail(button);

            if (button._thumbBlank)
            {
                stats.imgThumbnail.Hide();
            }
            else
            {
                stats.imgThumbnail.ChangeElement(thumbnailName);
                if (button.itf.mod.DLCMissing)
                    stats.imgThumbnail.sprite.shader = greyscaleShader;
                else
                    stats.imgThumbnail.sprite.shader = FShader.Basic;
                stats.imgThumbnail.Show();
            }
        });
        c.Emit(OpCodes.Br, destination); // skip over the vanilla code for loading the thumbnail
    }

    // The ModButton uses FTexture for the thumbnails, which is very inefficient, so we replace it with an FSprite, and replace all code to use this new sprite
    private static Dictionary<MenuModList.ModButton, FSprite> modButtonThumbnails = new Dictionary<MenuModList.ModButton, FSprite>();

    // Replaced FTexture with FSprite to significantly reduce thumbnail construction time
    private static void ModButton_Ctor_IL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        // _thumb = _thumbD.Clone();
        c.GotoNext(
            i => i.MatchLdarg(0),
            i => i.MatchLdsfld<MenuModList.ModButton>("_thumbD")
            );
        int start = c.Index; // IL_011a: ldarg.0
        c.GotoNext(i => i.MatchCallvirt<FNode>("MoveBehindOtherNode")); // IL_01f5: callvirt instance void FNode::MoveBehindOtherNode(class FNode)
        int end = c.Index + 1;

        c.Goto(start);
        c.RemoveRange(end - start); // removing thumbnail initialization code

        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate((MenuModList.ModButton button) =>
        {
            FAtlasElement element = Futile.atlasManager.GetElementWithName(ConfigContainer._GetThumbnailName(MenuModList.ModButton.RainWorldDummy.mod.id));
            FSprite thumbnail = new FSprite(element)
            {
                anchorX = 0f,
                anchorY = 0f,
                x = 125f - element.sourceSize.x * MenuModList.ModButton._thumbRatio / 2f - 6f,
                y = 9f,
                scaleX = MenuModList.ModButton._thumbRatio,
                scaleY = MenuModList.ModButton._thumbRatio
            };
            button.myContainer.AddChild(thumbnail);
            thumbnail.MoveBehindOtherNode(button._rectH.sprites[0].container);
            modButtonThumbnails[button] = thumbnail;
        });
    }

    // rewritten to load the thumbnail as needed and use FSprite
    private static void ModButton_ProcessThumbnail(On.Menu.Remix.MenuModList.ModButton.orig__ProcessThumbnail orig, MenuModList.ModButton self)
    {
        if (!Options.LoadThumbnails.Value)
        {
            self._thumbBlank = true;
            return;
        }

        try
        {
            if (!self._thumbLoaded)
                LoadModThumbnail(self);

            if (!self._thumbBlank)
                modButtonThumbnails[self].SetElementByName(ConfigContainer._GetThumbnailName(self.itf.mod.id));

            self._thumbProcessed = true;
            self._UpdateThumbnail();
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    // rewritten to use shaders for the greyscale effect
    private static void ModButton_UpdateThumbnail(On.Menu.Remix.MenuModList.ModButton.orig__UpdateThumbnail orig, MenuModList.ModButton self)
    {
        if (!self._thumbBlank)
        {
            if (self.selectEnabled)
                modButtonThumbnails[self].shader = FShader.Basic;
            else
                modButtonThumbnails[self].shader = greyscaleShader;
        }
    }

    // fix a bug with black bars appearing over the modlist
    // replace all instances of the FTexture _thumbnail with the new FSprite thumbnail
    private static void ModButton_GrafUpdate_IL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        // Get the thumbnail 
        FSprite thumbnail = null;
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate((MenuModList.ModButton button) => { thumbnail = modButtonThumbnails[button]; });

        // fix black bars everywhere when expanding the mod buttons
        c.GotoNext( // if (_fade >= 1f)
            i => i.Match(OpCodes.Blt_Un_S)
            );
        c.GotoNext();
        c.Emit(OpCodes.Ldarg_0);
        c.EmitDelegate((MenuModList.ModButton button) =>
        {
            button._glow.alpha = 0f;
        });

        // replace all instances of _thumbnail with my new thumbnail object
        while (c.TryGotoNext(
            i => i.MatchLdarg(0),
            i => i.MatchLdfld<MenuModList.ModButton>("_thumbnail")
            ))
        {
            c.RemoveRange(2);
            c.EmitDelegate(() => { return thumbnail; });
        }
    }

    private static void ModButton_UnloadUI_IL(ILContext il)
    {
        ILCursor c = new ILCursor(il);

        c.GotoNext( // _thumbnail.Destroy();
            i => i.MatchLdfld<MenuModList.ModButton>("_thumbnail"),
            i => i.MatchCallOrCallvirt<FTexture>("Destroy")
            );
        c.RemoveRange(2); // this code errors because _thumbnail is null (intentional)

        // unload the new FSprite thumbnail
        c.EmitDelegate((MenuModList.ModButton self) =>
        {
            modButtonThumbnails.Remove(self);
        }); 
    }

    private static void LoadModThumbnail(MenuModList.ModButton button)
    {
        Logger.LogInfo($"Loading mod thumbnail: {button.itf.mod.id}");

        try
        {
            ModManager.Mod mod = button.itf.mod;
            string name = ConfigContainer._GetThumbnailName(mod.id);
            if (Futile.atlasManager.DoesContainElementWithName(name))
            {
                button._PingThumbnailLoaded();
                return;
            }

            if (string.IsNullOrEmpty(mod.basePath) && string.IsNullOrEmpty(mod.path))
            {
                button._PingThumbnailLoaded(true);
                return;
            }

            string thumbnailPath = mod.GetThumbnailPath();
            if (!File.Exists(thumbnailPath))
            {
                button._PingThumbnailLoaded(true);
                return;
            }

			byte[] data = File.ReadAllBytes(thumbnailPath);
			Texture2D texture = new Texture2D(1, 1, TextureFormat.ARGB32, mipChain: false);
			texture.LoadImage(data);
            if (texture.width != 426 || texture.height != 240)
            {
                Logger.LogWarning($"Thumbnail has incorrect dimensions - {thumbnailPath}");
                TextureScale.Bilinear(texture, 426, 240);
            }
            ConfigContainer._TrimModThumbnail(ref texture);
			HeavyTexturesCache.LoadAndCacheAtlasFromTexture(name, texture, textureFromAsset: false);
            button._PingThumbnailLoaded();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            button._PingThumbnailLoaded(true);
        }
    }
}
