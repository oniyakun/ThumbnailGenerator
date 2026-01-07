using System.IO;
using UnityEditor;
using UnityEngine;

namespace ThumbnailGeneratorPlugin
{
    public class ThumbnailGenerator
    {
        private const int ThumbnailWidth = 256;
        private const int ThumbnailHeight = 256;
        
        private const string ThumbnailCameraLayer = "DT_Thumbnail";
        private const string ThumbnailCameraName = "DTTempThumbnailCamera";
        private const string ThumbnailWearableName = "DTTempThumbnailWearable";
        private const float ThumbnailCameraFov = 45.0f;
        private const int StartingUserLayer = 8;
        private const int MaxLayers = 32;

        [MenuItem("GameObject/生成缩略图", false, 10)]
        public static void GenerateThumbnailMenu()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
            {
                EditorUtility.DisplayDialog("Error", "Please select a GameObject first.", "OK");
                return;
            }

            string relativePath = "Oniya/ThumbnailGenerator/GeneratedImages";
            string saveDir = Path.Combine(Application.dataPath, relativePath);
            
            if (!Directory.Exists(saveDir))
            {
                Directory.CreateDirectory(saveDir);
            }
            
            CaptureAndSave(selected, saveDir);
        }

        /// <summary>
        /// Generates a thumbnail for the given object and saves it to the specified directory.
        /// Returns the loaded Texture2D asset if inside a Unity project, or null/texture instance otherwise.
        /// </summary>
        public static Texture2D CaptureAndSave(GameObject target, string saveDirectory)
        {
            if (target == null) return null;

            Texture2D texture = null;
            RenderTexture renderTexture = null;
            GameObject cameraObj = null;
            GameObject clone = null;
            GameObject lightObj = null;

            try
            {
                if (!PrepareWearableThumbnailCameraLayer())
                    Debug.LogWarning("Could not allocate a layer for thumbnail generation.");

                renderTexture = new RenderTexture(ThumbnailWidth, ThumbnailHeight, 24);
                cameraObj = new GameObject(ThumbnailCameraName);
                var camera = cameraObj.AddComponent<Camera>();
                
                camera.targetTexture = renderTexture;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0, 0, 0, 0);
                camera.fieldOfView = ThumbnailCameraFov;
                camera.cullingMask = LayerMask.GetMask(ThumbnailCameraLayer);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 1000f;

                clone = Object.Instantiate(target);
                clone.name = ThumbnailWearableName;
                var clonePos = new Vector3(0, -1000, 0);
                clone.transform.position = clonePos;
                RecursiveSetLayer(clone, LayerMask.NameToLayer(ThumbnailCameraLayer));

                // Bounds Calculation & Positioning
                var renderers = clone.GetComponentsInChildren<Renderer>();
                Bounds bounds = new Bounds();
                bool hasBounds = false;
                
                foreach (var r in renderers)
                {
                    if (!r.enabled) continue;
                    if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                    else bounds.Encapsulate(r.bounds);
                }

                if (!hasBounds) bounds = new Bounds(clone.transform.position, Vector3.one * 0.1f);

                Vector3 objectCenter = bounds.center;
                float maxDim = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                if (maxDim <= 0.001f) maxDim = 0.1f;

                float distance = (maxDim * 0.6f) / Mathf.Tan(ThumbnailCameraFov * 0.5f * Mathf.Deg2Rad);
                float minSafeDistance = (maxDim / 2.0f) + camera.nearClipPlane + 0.02f;
                distance = Mathf.Max(distance, minSafeDistance);

                cameraObj.transform.position = objectCenter + new Vector3(0, 0, distance);
                cameraObj.transform.LookAt(objectCenter);

                // Add Light
                lightObj = new GameObject("TempLight");
                var light = lightObj.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                light.color = Color.white;
                lightObj.transform.SetParent(cameraObj.transform);
                lightObj.transform.localRotation = Quaternion.identity;

                Physics.SyncTransforms();

                // Pass 1
                camera.Render();
                
                RenderTexture.active = renderTexture;
                texture = new Texture2D(ThumbnailWidth, ThumbnailHeight, TextureFormat.ARGB32, false);
                texture.ReadPixels(new Rect(0, 0, ThumbnailWidth, ThumbnailHeight), 0, 0);
                texture.Apply();
                RenderTexture.active = null;

                // Pass 2: Auto Framing
                Rect contentRect = CalculateVisiblePixelsRect(texture);
                
                if (contentRect.width > 0 && contentRect.height > 0)
                {
                    Vector2 currentCenter = contentRect.center;
                    Vector2 centerOffset = currentCenter - new Vector2(0.5f, 0.5f);
                    float contentMaxDim = Mathf.Max(contentRect.width, contentRect.height);
                    float targetFill = 0.85f;
                    
                    if (contentMaxDim < targetFill * 0.8f || Mathf.Abs(centerOffset.x) > 0.1f || Mathf.Abs(centerOffset.y) > 0.1f)
                    {
                        float visibleHeightAtDist = 2.0f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                        float visibleWidthAtDist = visibleHeightAtDist * camera.aspect;
                        
                        Vector3 moveOffset = camera.transform.right * (centerOffset.x * visibleWidthAtDist) + 
                                             camera.transform.up * (centerOffset.y * visibleHeightAtDist);
                        
                        cameraObj.transform.position += moveOffset;
                        
                        float zoomFactor = contentMaxDim / targetFill;
                        zoomFactor = Mathf.Max(zoomFactor, 0.1f); 
                        
                        float newDistance = distance * zoomFactor;
                        newDistance = Mathf.Max(newDistance, 0.15f);
                        
                        cameraObj.transform.position += cameraObj.transform.forward * (distance - newDistance);
                        
                        camera.Render();
                        
                        RenderTexture.active = renderTexture;
                        texture.ReadPixels(new Rect(0, 0, ThumbnailWidth, ThumbnailHeight), 0, 0);
                        texture.Apply();
                        RenderTexture.active = null;
                    }
                }

                byte[] bytes = texture.EncodeToPNG();
                string filename = $"{target.name}_Thumbnail.png";
                string fullPath = Path.Combine(saveDirectory, filename);
                
                File.WriteAllBytes(fullPath, bytes);
                Debug.Log($"Thumbnail generated at: {fullPath}");
                
                // Return Asset if possible
                if (fullPath.StartsWith(Application.dataPath))
                {
                    AssetDatabase.Refresh();
                    string relativePath = "Assets" + fullPath.Substring(Application.dataPath.Length);
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(relativePath);
                }
                
                return texture;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to generate thumbnail: {e.Message}\n{e.StackTrace}");
                return null;
            }
            finally
            {
                // Cleanup
                CleanUpThumbnailObjects(); 
                if (renderTexture != null) renderTexture.Release();
                // If we returned the texture instance (non-asset), we shouldn't destroy it immediately if it's used. 
                // But in Editor usage (AssetDatabase), we return the Asset, so we can destroy the temp texture.
                // However, CaptureAndSave returns the Asset if possible.
                // If it returned the raw Texture2D (not asset), caller manages it.
                // But texture created with new Texture2D() is unmanaged.
                // To be safe, if we loaded from AssetDatabase, we can destroy the temp 'texture'.
                if (texture != null && !EditorUtility.IsPersistent(texture)) 
                {
                    if (Application.isPlaying) Object.Destroy(texture);
                    else Object.DestroyImmediate(texture);
                }
            }
        }

        private static void RecursiveSetLayer(GameObject obj, int layerIndex)
        {
            if (obj.layer == 0) // Only change default layer
            {
                obj.layer = layerIndex;
            }

            for (var i = 0; i < obj.transform.childCount; i++)
            {
                var child = obj.transform.GetChild(i);
                RecursiveSetLayer(child.gameObject, layerIndex);
            }
        }

        public static void CleanUpThumbnailObjects()
        {
            var existingCamObj = GameObject.Find(ThumbnailCameraName);
            if (existingCamObj != null) 
            {
                if (Application.isPlaying) Object.Destroy(existingCamObj);
                else Object.DestroyImmediate(existingCamObj);
            }

            var existingDummy = GameObject.Find(ThumbnailWearableName);
            if (existingDummy != null)
            {
                if (Application.isPlaying) Object.Destroy(existingDummy);
                else Object.DestroyImmediate(existingDummy);
            }
        }

        public static bool PrepareWearableThumbnailCameraLayer()
        {
            if (!HasCullingLayer(ThumbnailCameraLayer))
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
                if (assets == null) return true; 
                
                var so = new SerializedObject(assets);
                var layers = so.FindProperty("layers");

                for (var i = MaxLayers - 1; i >= StartingUserLayer; i--)
                {
                    var layer = layers.GetArrayElementAtIndex(i);
                    if (string.IsNullOrEmpty(layer.stringValue))
                    {
                        layer.stringValue = ThumbnailCameraLayer;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        return true;
                    }
                }
                return false;
            }
            return true;
        }

        public static bool HasCullingLayer(string layerName)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
            if (assets == null) return false; 
            
            var so = new SerializedObject(assets);
            var layers = so.FindProperty("layers");
            for (var i = 0; i < MaxLayers; i++)
            {
                var elem = layers.GetArrayElementAtIndex(i);
                if (elem.stringValue.Equals(layerName))
                {
                    return true;
                }
            }
            return false;
        }

        private static Rect CalculateVisiblePixelsRect(Texture2D tex)
        {
            int w = tex.width;
            int h = tex.height;
            Color[] pixels = tex.GetPixels();
            int minX = w, maxX = 0, minY = h, maxY = 0;
            bool found = false;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a > 0.01f) 
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        found = true;
                    }
                }
            }
            if (!found) return new Rect(0, 0, 0, 0);
            float xNorm = (float)minX / w;
            float yNorm = (float)minY / h;
            float wNorm = (float)(maxX - minX + 1) / w;
            float hNorm = (float)(maxY - minY + 1) / h;
            return new Rect(xNorm, yNorm, wNorm, hNorm);
        }
    }
}
