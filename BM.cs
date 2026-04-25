using AIChara;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace UnderwearBodyMask
{
    [BepInPlugin("com.yourstudio.underwearbodymask", "Underwear Body Mask Plugin", "0.0.1")]
    public class BodyMaskPlugin : BaseUnityPlugin
    {
        private static ManualLogSource _log;
        private static string _logFilePath;

        private void Awake()
        {
            _log = Logger;
            _logFilePath = Path.Combine(Paths.BepInExRootPath, "BodyMaskPlugin.log");
            var harmony = new Harmony("com.yourstudio.underwearbodymask");

            TryPatch(harmony, AccessTools.Method(typeof(ChaControl), "ChangeCustomClothes"), nameof(Hooks.OnRefresh), "ChangeCustomClothes");

            TryPatch(harmony, AccessTools.Method(typeof(ChaControl), "Reload"), nameof(Hooks.OnRefresh), "Reload");

            TryPatch(harmony, AccessTools.Method(typeof(ChaControl), "SetClothesState"), nameof(Hooks.OnRefresh), "SetClothesState");

            foreach (var changeClothes in typeof(ChaControl).GetMethods()
                .Where(m => m.Name.StartsWith("ChangeClothes", StringComparison.Ordinal)))
            {
                bool isAsync = typeof(IEnumerator).IsAssignableFrom(changeClothes.ReturnType);
                TryPatch(harmony, changeClothes, isAsync ? nameof(Hooks.OnReloadAsync) : nameof(Hooks.OnRefresh), changeClothes.Name);
            }

            foreach (var bodyRefreshMethod in typeof(ChaControl).GetMethods()
                .Where(m => m.Name == "InitBaseCustomTextureBody"
                         || m.Name == "InitializeControlBodyObject"
                         || m.Name == "SetBodyBaseMaterial"
                         || m.Name == "CreateBodyTexture"
                         || m.Name == "ChangeCustomBodyWithoutCustomTexture"))
            {
                TryPatch(harmony, bodyRefreshMethod, nameof(Hooks.OnRefresh), bodyRefreshMethod.Name);
            }

            TryPatchUncensorSelector(harmony);

            var initialize = typeof(ChaControl)
                .GetMethods()
                .FirstOrDefault(m => m.Name == "Initialize" && m.GetParameters().Length >= 5);

            if (initialize != null)
                TryPatch(harmony, initialize, nameof(Hooks.OnRefresh), "Initialize(5+)");
            else
                LogWarning("[BodyMask] Initialize(5+) not found.");

            var reloadAsync = typeof(ChaControl)
                .GetMethods()
                .FirstOrDefault(m => m.Name == "ReloadAsync" && m.GetParameters().Length >= 5);

            if (reloadAsync != null)
                TryPatch(harmony, reloadAsync, nameof(Hooks.OnReloadAsync), "ReloadAsync(5+)");
            else
                LogWarning("[BodyMask] ReloadAsync(5+) not found.");

            var changeCoord = typeof(ChaControl)
                .GetMethods()
                .FirstOrDefault(m => m.Name == "ChangeNowCoordinate" && m.GetParameters().Length >= 3);

            if (changeCoord != null)
                TryPatch(harmony, changeCoord, nameof(Hooks.OnRefresh), "ChangeNowCoordinate(3+)");
            else
                LogWarning("[BodyMask] ChangeNowCoordinate(3+) not found.");
        }

        private static void TryPatch(Harmony harmony, System.Reflection.MethodBase method, string hookName, string label)
        {
            if (method == null)
            {
                LogWarning($"[BodyMask] Patch target missing: {label}");
                return;
            }

            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(Hooks), hookName));
                LogInfo($"[BodyMask] Patched {label}");
            }
            catch (Exception ex)
            {
                LogError($"[BodyMask] Failed patch {label}: {ex}");
            }
        }

        private static void TryPatchUncensorSelector(Harmony harmony)
        {
            var controllerType = AccessTools.TypeByName("KK_Plugins.UncensorSelector+UncensorSelectorController");
            if (controllerType == null)
            {
                LogInfo("[BodyMask] Uncensor Selector controller not found.");
                return;
            }

            LogInfo("[BodyMask] Uncensor Selector controller detected.");

            TryPatch(harmony, AccessTools.Method(controllerType, "ReloadCharacterBody"), nameof(Hooks.OnComponentRefresh), "UncensorSelector.ReloadCharacterBody");
            TryPatch(harmony, AccessTools.Method(controllerType, "UpdateSkin"), nameof(Hooks.OnComponentRefresh), "UncensorSelector.UpdateSkin");
            TryPatch(harmony, AccessTools.Method(controllerType, "ReloadCharacterUncensor"), nameof(Hooks.OnComponentReloadAsync), "UncensorSelector.ReloadCharacterUncensor");
        }

        public static void LogInfo(string message)
        {
            _log?.LogInfo(message);
            WriteToFile("INFO", message);
        }

        public static void LogWarning(string message)
        {
            _log?.LogWarning(message);
            WriteToFile("WARN", message);
        }

        public static void LogError(string message)
        {
            _log?.LogError(message);
            WriteToFile("ERROR", message);
        }

        private static void WriteToFile(string level, string message)
        {
            try
            {
                if (string.IsNullOrEmpty(_logFilePath)) return;
                File.AppendAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    public static class Hooks
    {
        public static void OnRefresh(ChaControl __instance)
        {
            RequestRefresh(__instance);
        }

        public static void OnReloadAsync(ChaControl __instance, ref IEnumerator __result)
        {
            var original = __result;
            __result = RunAfterCoroutine(original, () =>
            {
                RequestRefresh(__instance);
            });
        }

        public static void OnComponentRefresh(Component __instance)
        {
            RequestRefresh(__instance != null ? __instance.GetComponent<ChaControl>() : null);
        }

        public static void OnComponentReloadAsync(Component __instance, ref IEnumerator __result)
        {
            var original = __result;
            __result = RunAfterCoroutine(original, () =>
            {
                RequestRefresh(__instance != null ? __instance.GetComponent<ChaControl>() : null);
            });
        }

        private static IEnumerator RunAfterCoroutine(IEnumerator inner, Action callback)
        {
            if (inner != null) yield return inner;
            callback();
        }

        private static void RequestRefresh(ChaControl chaCtrl)
        {
            var applier = GetOrCreateApplier(chaCtrl);
            if (applier != null) applier.RequestRefresh();
        }

        public static BodyMaskApplier GetOrCreateApplier(ChaControl chaCtrl)
        {
            if (chaCtrl == null) return null;

            var applier = chaCtrl.gameObject.GetComponent<BodyMaskApplier>();
            if (applier == null)
                applier = chaCtrl.gameObject.AddComponent<BodyMaskApplier>();

            applier.ChaCtrl = chaCtrl;
            return applier;
        }
    }

    public class BodyMaskApplier : MonoBehaviour
    {
        private const int MaxRefreshFrames = 180;

        public ChaControl ChaCtrl;

        private Texture2D _maskTex;
        private Texture2D _outerTopMaskTex;
        private Texture2D _outerBottomMaskTex;
        private Texture2D _innerTopMaskTex;
        private Texture2D _innerBottomMaskTex;
        private bool _refreshPending;
        private int _refreshTicket;
        private Coroutine _refreshCoroutine;
        private readonly int[] _lastClothesIds = new int[7];
        private int _lastBodyMeshId;
        private Mesh _lastBodyMesh;
        private string _cachedMaskKey;
        private Texture2D _cachedMaskTex;
        private Texture _fallbackBodyAlphaMask;
        private Texture _fallbackBodyBAlphaMask;
        private Texture _fallbackBraAlphaMask;
        private Texture _fallbackInnerBAlphaMask;
        private Texture _fallbackInnerTBAlphaMask;
        private bool _customMaskApplied;

        public void RequestRefresh()
        {
            _refreshPending = true;
            _refreshTicket++;
        }

        private void OnDisable()
        {
            if (_refreshCoroutine == null) return;

            StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
        }

        private void Update()
        {
            bool changed = false;

            if (ChaCtrl?.nowCoordinate?.clothes?.parts != null)
            {
                var parts = ChaCtrl.nowCoordinate.clothes.parts;
                for (int i = 0; i < parts.Length && i < _lastClothesIds.Length; i++)
                {
                    int currentId = parts[i]?.id ?? 0;
                    if (_lastClothesIds[i] != currentId)
                    {
                        _lastClothesIds[i] = currentId;
                        changed = true;
                    }
                }
            }

            if (ChaCtrl?.objBody != null)
            {
                var rend = ChaCtrl.objBody
                    .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .FirstOrDefault(r => r != null && r.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0);

                if (rend != null)
                {
                    int currentMeshId = rend.GetInstanceID();
                    Mesh currentMesh = rend.sharedMesh; // FIX: US tráo SharedMesh khi đổi Body

                    if (_lastBodyMeshId != currentMeshId || _lastBodyMesh != currentMesh)
                    {
                        _lastBodyMeshId = currentMeshId;
                        _lastBodyMesh = currentMesh;
                        _customMaskApplied = false;
                        changed = true;
                    }
                }
            }

            if (changed) RequestRefresh();

            if (!_refreshPending) return;
            _refreshPending = false;

            if (_refreshCoroutine != null)
                StopCoroutine(_refreshCoroutine);

            _refreshCoroutine = StartCoroutine(RefreshAfterDelay(_refreshTicket));
        }

        private IEnumerator RefreshAfterDelay(int refreshTicket)
        {
            for (int frame = 0; frame < MaxRefreshFrames; frame++)
            {
                yield return null;

                if (refreshTicket != _refreshTicket)
                {
                    _refreshCoroutine = null;
                    yield break;
                }

                if (!IsLookupReady()) continue;

                RefreshMaskState();
                if (HasCustomInnerMask())
                {
                    _refreshCoroutine = null;
                    BodyMaskPlugin.LogInfo($"[BodyMask] Inner mask ready: {_maskTex.name} {_maskTex.width}x{_maskTex.height}");
                    yield break;
                }
            }
            ClearMaskState();
            _refreshCoroutine = null;
            BodyMaskPlugin.LogInfo("[BodyMask] No custom inner mask found for current outfit after waiting.");
        }

        private void LateUpdate()
        {
            if (ChaCtrl == null) return;

            if (!HasCustomInnerMask())
            {
                if (_customMaskApplied)
                    RestoreBodyAlphaMask();
                return;
            }

            ApplyMaskToBody(_maskTex);
        }

        private bool IsLookupReady()
        {
            return ChaCtrl != null
                && ChaCtrl.lstCtrl != null
                && ChaCtrl.nowCoordinate?.clothes?.parts != null;
        }

        private Texture2D FindMask()
        {
            foreach (int kind in new[] { 2, 3, 0, 1 })
            {
                var tex = GetMaskFromSlot(kind);
                if (tex != null) return tex;
            }

            return null;
        }

        private Texture2D GetMaskFromSlot(int kind)
        {
            var parts = ChaCtrl?.nowCoordinate?.clothes?.parts;
            if (parts == null || kind >= parts.Length || ChaCtrl.lstCtrl == null) return null;

            var clothesInfo = parts[kind];
            if (clothesInfo == null || clothesInfo.id == 0) return null;

            ListInfoBase listInfo = null;

            foreach (ChaListDefine.CategoryNo cat in Enum.GetValues(typeof(ChaListDefine.CategoryNo)))
            {
                var tempInfo = ChaCtrl.lstCtrl.GetListInfo(cat, clothesInfo.id);
                if (tempInfo == null) continue;

                if (clothesInfo.id > 100000)
                {
                    listInfo = tempInfo;
                    break;
                }

                string catName = cat.ToString().ToLowerInvariant();
                bool match = false;
                switch (kind)
                {
                    case 0:
                        match = catName.Contains("top") || catName.Contains("outer") || catName.Contains("swim") || catName.Contains("fin");
                        break;
                    case 1:
                        match = catName.Contains("bot") || catName.Contains("skirt") || catName.Contains("pant");
                        break;
                    case 2:
                        match = catName.Contains("bra") || catName.Contains("inner") || catName.Contains("up");
                        break;
                    case 3:
                        match = catName.Contains("short") || catName.Contains("bot") || catName.Contains("inner") || catName.Contains("low");
                        break;
                }

                if (match)
                {
                    listInfo = tempInfo;
                    break;
                }
            }

            if (listInfo == null) return null;

            string bundlePath = null;
            string texName = null;

            foreach (var kvp in listInfo.dictInfo)
            {
                string val = kvp.Value;
                if (string.IsNullOrEmpty(val)) continue;

                string lowerVal = val.ToLowerInvariant();
                if (val.Contains("_bm.unity3d") || val.Contains("-bm.unity3d"))
                    bundlePath = val;
                else if (val == "AlphaMask" || lowerVal.Contains("bodymask") || lowerVal.Contains("alphamask"))
                    texName = val;
            }

            if (string.IsNullOrEmpty(bundlePath) || string.IsNullOrEmpty(texName)) return null;

            string cacheKey = bundlePath + "|" + texName;
            if (_cachedMaskTex != null && string.Equals(_cachedMaskKey, cacheKey, StringComparison.Ordinal))
                return _cachedMaskTex;

            var mask = CommonLib.LoadAsset<Texture2D>(bundlePath, texName, false, "");
            if (mask != null)
            {
                _cachedMaskKey = cacheKey;
                _cachedMaskTex = mask;
            }

            return mask;
        }

        private void RefreshMaskState()
        {
            _innerTopMaskTex = GetMaskFromSlot(2);
            _innerBottomMaskTex = GetMaskFromSlot(3);
            _outerTopMaskTex = GetMaskFromSlot(0);
            _outerBottomMaskTex = GetMaskFromSlot(1);
            _maskTex = GetPreferredMask();
        }

        private void ClearMaskState()
        {
            _maskTex = null;
            _outerTopMaskTex = null;
            _outerBottomMaskTex = null;
            _innerTopMaskTex = null;
            _innerBottomMaskTex = null;
        }

        private bool HasCustomInnerMask()
            => _innerTopMaskTex != null || _innerBottomMaskTex != null;

        private Texture2D GetPreferredMask()
            => _innerTopMaskTex ?? _innerBottomMaskTex ?? _outerTopMaskTex ?? _outerBottomMaskTex;

        private void ApplyMaskToBody(Texture maskTex)
        {
            if (ChaCtrl == null || maskTex == null) return;

            if (!_customMaskApplied)
                CaptureFallbackAlphaMasks();

            bool slotMaskChanged = false;
            Texture topMask = _innerTopMaskTex;
            Texture bottomMask = _innerBottomMaskTex;

            if (topMask != null && ChaCtrl.texBraAlphaMask != topMask)
            {
                ChaCtrl.texBraAlphaMask = topMask;
                slotMaskChanged = true;
            }

            if (topMask != null && ChaCtrl.texInnerTBAlphaMask != topMask)
            {
                ChaCtrl.texInnerTBAlphaMask = topMask;
                slotMaskChanged = true;
            }

            if (topMask != null && ChaCtrl.texBodyAlphaMask != topMask)
            {
                ChaCtrl.texBodyAlphaMask = topMask;
                slotMaskChanged = true;
            }

            if (bottomMask != null && ChaCtrl.texInnerBAlphaMask != bottomMask)
            {
                ChaCtrl.texInnerBAlphaMask = bottomMask;
                slotMaskChanged = true;
            }

            if (bottomMask != null && ChaCtrl.texBodyBAlphaMask != bottomMask)
            {
                ChaCtrl.texBodyBAlphaMask = bottomMask;
                slotMaskChanged = true;
            }

            if (slotMaskChanged)
            {
                ChaCtrl.updateAlphaMask = true;
                ChaCtrl.updateAlphaMask2 = true;
                RefreshGameAlphaMask();
            }

            Texture resolvedBodyMask = ChaCtrl.texBodyAlphaMask != null ? ChaCtrl.texBodyAlphaMask : maskTex;
            Texture resolvedBodyBMask = ChaCtrl.texBodyBAlphaMask;

            if (ChaCtrl.customMatBody != null && MaterialNeedsBodyMaskUpdate(ChaCtrl.customMatBody, resolvedBodyMask, resolvedBodyBMask))
                ApplyBodyMaskToMaterial(ChaCtrl.customMatBody, resolvedBodyMask, resolvedBodyBMask);

            if (ChaCtrl.objBody != null)
            {
                var renderers = ChaCtrl.objBody.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var rend in renderers)
                {
                    if (rend == null) continue;
                    if (rend.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var sharedMats = rend.sharedMaterials;
                    bool needsUpdate = sharedMats.Any(mat => MaterialNeedsBodyMaskUpdate(mat, resolvedBodyMask, resolvedBodyBMask));
                    if (!needsUpdate) continue;

                    var mats = rend.materials;
                    foreach (var mat in mats)
                        ApplyBodyMaskToMaterial(mat, resolvedBodyMask, resolvedBodyBMask);

                    rend.materials = mats;
                }
            }

            _customMaskApplied = true;
        }

        private void RestoreBodyAlphaMask()
        {
            if (ChaCtrl == null) return;

            bool slotMaskChanged = false;

            if (ChaCtrl.texBodyAlphaMask != _fallbackBodyAlphaMask)
            {
                ChaCtrl.texBodyAlphaMask = _fallbackBodyAlphaMask;
                slotMaskChanged = true;
            }

            if (ChaCtrl.texBodyBAlphaMask != _fallbackBodyBAlphaMask)
            {
                ChaCtrl.texBodyBAlphaMask = _fallbackBodyBAlphaMask;
                slotMaskChanged = true;
            }

            if (ChaCtrl.texBraAlphaMask != _fallbackBraAlphaMask)
            {
                ChaCtrl.texBraAlphaMask = _fallbackBraAlphaMask;
                slotMaskChanged = true;
            }

            if (ChaCtrl.texInnerBAlphaMask != _fallbackInnerBAlphaMask)
            {
                ChaCtrl.texInnerBAlphaMask = _fallbackInnerBAlphaMask;
                slotMaskChanged = true;
            }

            if (ChaCtrl.texInnerTBAlphaMask != _fallbackInnerTBAlphaMask)
            {
                ChaCtrl.texInnerTBAlphaMask = _fallbackInnerTBAlphaMask;
                slotMaskChanged = true;
            }

            if (slotMaskChanged)
            {
                ChaCtrl.updateAlphaMask = true;
                ChaCtrl.updateAlphaMask2 = true;
                RefreshGameAlphaMask();
            }

            Texture restoreMask = ChaCtrl.texBodyAlphaMask;
            Texture restoreMaskB = ChaCtrl.texBodyBAlphaMask;

            if (ChaCtrl.customMatBody != null && MaterialNeedsBodyMaskUpdate(ChaCtrl.customMatBody, restoreMask, restoreMaskB))
                ApplyBodyMaskToMaterial(ChaCtrl.customMatBody, restoreMask, restoreMaskB);

            if (ChaCtrl.objBody != null)
            {
                var renderers = ChaCtrl.objBody.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var rend in renderers)
                {
                    if (rend == null) continue;
                    if (rend.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var sharedMats = rend.sharedMaterials;
                    bool needsUpdate = sharedMats.Any(mat => MaterialNeedsBodyMaskUpdate(mat, restoreMask, restoreMaskB));
                    if (!needsUpdate) continue;

                    var mats = rend.materials;
                    foreach (var mat in mats)
                        ApplyBodyMaskToMaterial(mat, restoreMask, restoreMaskB);

                    rend.materials = mats;
                }
            }

            _customMaskApplied = false;
        }

        private void CaptureFallbackAlphaMasks()
        {
            _fallbackBodyAlphaMask = ChaCtrl.texBodyAlphaMask;
            _fallbackBodyBAlphaMask = ChaCtrl.texBodyBAlphaMask;
            _fallbackBraAlphaMask = ChaCtrl.texBraAlphaMask;
            _fallbackInnerBAlphaMask = ChaCtrl.texInnerBAlphaMask;
            _fallbackInnerTBAlphaMask = ChaCtrl.texInnerTBAlphaMask;
        }

        private void RefreshGameAlphaMask()
        {
            try
            {
                if (ChaCtrl.fileStatus?.clothesState != null)
                    ChaCtrl.ChangeAlphaMask(ChaCtrl.fileStatus.clothesState);
            }
            catch (Exception ex)
            {
                BodyMaskPlugin.LogWarning($"[BodyMask] ChangeAlphaMask failed: {ex.Message}");
            }

            try
            {
                ChaCtrl.ChangeAlphaMaskEx();
            }
            catch (Exception ex)
            {
                BodyMaskPlugin.LogWarning($"[BodyMask] ChangeAlphaMaskEx failed: {ex.Message}");
            }

            try
            {
                ChaCtrl.ChangeAlphaMask2();
            }
            catch (Exception ex)
            {
                BodyMaskPlugin.LogWarning($"[BodyMask] ChangeAlphaMask2 failed: {ex.Message}");
            }

            try
            {
                ChaCtrl.UpdateVisible();
            }
            catch (Exception ex)
            {
                BodyMaskPlugin.LogWarning($"[BodyMask] UpdateVisible failed: {ex.Message}");
            }
        }

        private static bool MaterialNeedsBodyMaskUpdate(Material mat, Texture desiredMask, Texture desiredMask2)
        {
            if (mat == null) return false;

            bool hasAlphaMask = mat.HasProperty(ChaShader.AlphaMask) || mat.HasProperty("_AlphaMask");
            bool hasAlphaMask2 = mat.HasProperty(ChaShader.AlphaMask2) || mat.HasProperty("_AlphaMask2");
            if (!hasAlphaMask && !hasAlphaMask2) return false;

            if (mat.HasProperty(ChaShader.AlphaMask) && mat.GetTexture(ChaShader.AlphaMask) != desiredMask) return true;
            if (mat.HasProperty("_AlphaMask") && mat.GetTexture("_AlphaMask") != desiredMask) return true;
            if (mat.HasProperty(ChaShader.AlphaMask2) && mat.GetTexture(ChaShader.AlphaMask2) != desiredMask2) return true;
            if (mat.HasProperty("_AlphaMask2") && mat.GetTexture("_AlphaMask2") != desiredMask2) return true;

            bool enable = desiredMask != null || desiredMask2 != null;
            if (mat.HasProperty("_AlphaMaskEnable") && mat.GetFloat("_AlphaMaskEnable") != (enable ? 1f : 0f)) return true;
            if (mat.HasProperty("_alpha_a") && mat.GetFloat("_alpha_a") != (enable ? 1f : 0f)) return true;
            if (enable && (!mat.IsKeywordEnabled("_ALPHAMASK_ON") || !mat.IsKeywordEnabled("ALPHAMASK_ON"))) return true;
            if (!enable && (mat.IsKeywordEnabled("_ALPHAMASK_ON") || mat.IsKeywordEnabled("ALPHAMASK_ON"))) return true;

            return false;
        }

        private static void ApplyBodyMaskToMaterial(Material mat, Texture maskTex, Texture maskTex2)
        {
            if (mat == null) return;

            bool hasAlphaMask = mat.HasProperty(ChaShader.AlphaMask) || mat.HasProperty("_AlphaMask");
            bool hasAlphaMask2 = mat.HasProperty(ChaShader.AlphaMask2) || mat.HasProperty("_AlphaMask2");
            if (!hasAlphaMask && !hasAlphaMask2) return;

            if (mat.HasProperty(ChaShader.AlphaMask)) mat.SetTexture(ChaShader.AlphaMask, maskTex);
            if (mat.HasProperty("_AlphaMask")) mat.SetTexture("_AlphaMask", maskTex);
            if (mat.HasProperty(ChaShader.AlphaMask2)) mat.SetTexture(ChaShader.AlphaMask2, maskTex2);
            if (mat.HasProperty("_AlphaMask2")) mat.SetTexture("_AlphaMask2", maskTex2);

            bool enable = maskTex != null || maskTex2 != null;
            if (enable)
            {
                if (!mat.IsKeywordEnabled("_ALPHAMASK_ON")) mat.EnableKeyword("_ALPHAMASK_ON");
                if (!mat.IsKeywordEnabled("ALPHAMASK_ON")) mat.EnableKeyword("ALPHAMASK_ON");
            }
            else
            {
                mat.DisableKeyword("_ALPHAMASK_ON");
                mat.DisableKeyword("ALPHAMASK_ON");
            }

            if (mat.HasProperty("_alpha_a")) mat.SetFloat("_alpha_a", enable ? 1f : 0f);
            if (mat.HasProperty("_AlphaMaskEnable")) mat.SetFloat("_AlphaMaskEnable", enable ? 1f : 0f);
        }
    }
}