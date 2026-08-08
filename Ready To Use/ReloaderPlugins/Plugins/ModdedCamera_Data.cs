using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Xml.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Newtonsoft.Json;

namespace ModdedCamera
{
    public static class PathManager
    {
        private static readonly string PathsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "Paths");
        private const string JsonExtension = ".json";
        private const string XmlExtension = ".xml";
        private static readonly XmlSerializer PathXmlSerializer = new XmlSerializer(typeof(CameraPath));
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings();
        private static bool _jsonSettingsInitialized = false;

        private static void EnsureJsonSettings()
        {
            if (!_jsonSettingsInitialized)
            {
                JsonSettings.Formatting = Formatting.Indented;
                JsonSettings.TypeNameHandling = TypeNameHandling.None;
                JsonSettings.NullValueHandling = NullValueHandling.Ignore;
                JsonSettings.Converters = new List<JsonConverter> { new Vector3JsonConverter() };
                _jsonSettingsInitialized = true;
            }
        }

        static PathManager()
        {
            if (!Directory.Exists(PathsFolder))
            {
                Directory.CreateDirectory(PathsFolder);
            }
            MigrateXmlToJson();
        }

        private static void MigrateXmlToJson()
        {
            try
            {
                EnsureJsonSettings();
                string[] xmlFiles = Directory.GetFiles(PathsFolder, "*" + XmlExtension);
                int migrated = 0;
                foreach (string xmlFile in xmlFiles)
                {
                    try
                    {
                        CameraPath path;
                        using (StreamReader reader = new StreamReader(xmlFile))
                        {
                            path = (CameraPath)PathXmlSerializer.Deserialize(reader);
                        }
                        string jsonFile = Path.ChangeExtension(xmlFile, JsonExtension);
                        string json = JsonConvert.SerializeObject(path, JsonSettings);
                        File.WriteAllText(jsonFile, json);
                        File.Delete(xmlFile);
                        migrated++;
                        Logger.Info("Migrated: " + Path.GetFileName(xmlFile) + " to JSON");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to migrate XML file: " + xmlFile);
                    }
                }
                if (migrated > 0) Logger.Info("XML->JSON migration complete. " + migrated + " file(s).");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during XML->JSON migration");
            }
        }

        public static string SavePath(CameraPath path)
        {
            try
            {
                EnsureJsonSettings();
                if (path == null)
                {
                    Logger.Error("SavePath: path is null");
                    return null;
                }
                if (string.IsNullOrEmpty(path.Name))
                {
                    Logger.Error("SavePath: path name is empty");
                    return null;
                }
                path.Version = 1;
                string fileName = SanitizeFileName(path.Name) + JsonExtension;
                string filePath = Path.Combine(PathsFolder, fileName);
                string json = JsonConvert.SerializeObject(path, JsonSettings);
                File.WriteAllText(filePath, json);
                Logger.Info("SavePath: Saved " + fileName);
                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save path");
                return null;
            }
        }

        public static CameraPath LoadPath(string pathName)
        {
            EnsureJsonSettings();
            string fileName = SanitizeFileName(pathName) + JsonExtension;
            string filePath = Path.Combine(PathsFolder, fileName);
            if (!File.Exists(filePath))
            {
                string xmlFile = Path.ChangeExtension(filePath, XmlExtension);
                if (File.Exists(xmlFile))
                {
                    try
                    {
                        using (StreamReader reader = new StreamReader(xmlFile))
                        {
                            CameraPath path = (CameraPath)PathXmlSerializer.Deserialize(reader);
                            return ApplyBackwardCompatibility(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to load fallback XML path: " + pathName);
                        return null;
                    }
                }
                return null;
            }
            try
            {
                string json = File.ReadAllText(filePath);
                CameraPath path = JsonConvert.DeserializeObject<CameraPath>(json, JsonSettings);
                return ApplyBackwardCompatibility(path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load path: " + pathName);
                return null;
            }
        }

        public static bool PathExists(string pathName)
        {
            string fileName = SanitizeFileName(pathName);
            return File.Exists(Path.Combine(PathsFolder, fileName + JsonExtension)) ||
                   File.Exists(Path.Combine(PathsFolder, fileName + XmlExtension));
        }

        public static List<string> GetAllSavedPaths()
        {
            List<string> paths = new List<string>();
            if (!Directory.Exists(PathsFolder)) return paths;
            string[] jsonFiles = Directory.GetFiles(PathsFolder, "*" + JsonExtension);
            foreach (string file in jsonFiles)
            {
                paths.Add(Path.GetFileNameWithoutExtension(file));
            }
            string[] xmlFiles = Directory.GetFiles(PathsFolder, "*" + XmlExtension);
            foreach (string file in xmlFiles)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!paths.Contains(name)) paths.Add(name);
            }
            return paths;
        }

        public static bool DeletePath(string pathName)
        {
            string fileName = SanitizeFileName(pathName);
            string jsonPath = Path.Combine(PathsFolder, fileName + JsonExtension);
            string xmlPath = Path.Combine(PathsFolder, fileName + XmlExtension);
            if (File.Exists(jsonPath))
            {
                File.Delete(jsonPath);
                return true;
            }
            if (File.Exists(xmlPath))
            {
                File.Delete(xmlPath);
                return true;
            }
            return false;
        }

        public static bool RenamePath(string oldName, string newName)
        {
            try
            {
                if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;
                CameraPath path = LoadPath(oldName);
                if (path == null) return false;
                path.Name = newName;
                if (DeletePath(oldName))
                {
                    string result = SavePath(path);
                    return result != null;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to rename path");
                return false;
            }
        }

        private static CameraPath ApplyBackwardCompatibility(CameraPath path)
        {
            if (path == null) return null;
            if (path.NodeColors == null) path.NodeColors = new List<int>();
            if (path.Version >= 1) return path;

            if (path.Fov <= 0) path.Fov = 50;
            if (path.Speed <= 0) path.Speed = 1.0f;
            if (path.DefaultDuration <= 0) path.DefaultDuration = 5000;
            if (path.InterpolationMode != 0 && path.InterpolationMode != 2)
                path.InterpolationMode = 2;
            if (path.Durations == null || path.Durations.Count == 0)
            {
                path.Durations = new List<int>();
                int nodeCount = (path.Positions != null) ? path.Positions.Count : 0;
                for (int i = 0; i < nodeCount; i++)
                    path.Durations.Add(path.DefaultDuration);
            }
            // Backward compat: old paths without NodeInterpolationModes
            if (path.NodeInterpolationModes == null || path.NodeInterpolationModes.Count == 0)
            {
                // Convert old int Speed (1-100, normal=3) to new float multiplier (normal=1.0)
                path.Speed = path.Speed / 3.0f;
                // Snap to nearest valid speed value
                float nearestSpd = Utils.ValidSpeeds[0];
                float minDiffSpd = Math.Abs(path.Speed - nearestSpd);
                for (int si = 1; si < Utils.ValidSpeeds.Length; si++)
                {
                    float diffSpd = Math.Abs(path.Speed - Utils.ValidSpeeds[si]);
                    if (diffSpd < minDiffSpd)
                    {
                        minDiffSpd = diffSpd;
                        nearestSpd = Utils.ValidSpeeds[si];
                    }
                }
                path.Speed = nearestSpd;
                path.NodeInterpolationModes = new List<int>();
                int nodeCount = (path.Positions != null) ? path.Positions.Count : 0;
                for (int i = 0; i < nodeCount; i++)
                    path.NodeInterpolationModes.Add(path.InterpolationMode);
            }
            path.Version = 1;
            return path;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed_path";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }

    public static class CameraRenderer
    {
        public static void UpdateFocusArea(Vector3 position)
        {
            try
            {
                Function.Call(NativeHashes.SET_FOCUS_AREA, position.X, position.Y, position.Z);
            }
            catch (Exception ex)
            {
                Logger.Debug("SET_FOCUS_AREA warning: " + ex.Message);
            }
        }

        public static void ClearFocus()
        {
            try
            {
                Function.Call(NativeHashes.SET_FOCUS_AREA, 0f, 0f, 0f);
            }
            catch (Exception ex)
            {
                Logger.Debug("ClearFocus warning: " + ex.Message);
            }
        }

        public static void DrawPositionMarker(Vector3 cameraPos, Vector3 previousPos)
        {
            try
            {
                Vector3 direction = Vector3.Subtract(cameraPos, previousPos);
                Function.Call(NativeHashes.DRAW_MARKER_SPRITE, cameraPos.X, cameraPos.Y, cameraPos.Z, direction.X, direction.Y, direction.Z);
            }
            catch (Exception ex)
            {
                Logger.Debug("Draw marker warning: " + ex.Message);
            }
        }
    }

    public enum FadeState { None, FadingOut, Activating, FadingOutExit, Deactivating }

    public class FadeStateMachine
    {
        private readonly Action _onActivate;
        private readonly Action _onDeactivate;
        private readonly string _logPrefix;

        public FadeState State { get; private set; }

        public FadeStateMachine(Action onActivate, Action onDeactivate, string logPrefix)
        {
            this.State = FadeState.None;
            _onActivate = onActivate;
            _onDeactivate = onDeactivate;
            _logPrefix = logPrefix;
        }

        public void StartFadeOut(int fadeOutMs = 1200)
        {
            State = FadeState.FadingOut;
            Function.Call(Hash.DO_SCREEN_FADE_OUT, fadeOutMs);
        }

        public void StartFadeOutExit(int fadeOutMs = 1200)
        {
            State = FadeState.FadingOutExit;
            Function.Call(Hash.DO_SCREEN_FADE_OUT, fadeOutMs);
        }

        public void Update()
        {
            if (State == FadeState.None) return;
            try
            {
                if (State == FadeState.FadingOut)
                {
                    if (Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT))
                    {
                        if (_onActivate != null) _onActivate();
                        State = FadeState.Activating;
                        Function.Call(Hash.DO_SCREEN_FADE_IN, 800);
                    }
                }
                else if (State == FadeState.Activating)
                {
                    if (Function.Call<bool>(Hash.IS_SCREEN_FADED_IN))
                        State = FadeState.None;
                }
                else if (State == FadeState.FadingOutExit)
                {
                    if (Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT))
                    {
                        if (_onDeactivate != null) _onDeactivate();
                        State = FadeState.Deactivating;
                        Function.Call(Hash.DO_SCREEN_FADE_IN, 800);
                    }
                }
                else if (State == FadeState.Deactivating)
                {
                    if (Function.Call<bool>(Hash.IS_SCREEN_FADED_IN))
                        State = FadeState.None;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, _logPrefix + " UpdateFade: Unexpected error");
                State = FadeState.None;
            }
        }

        public void Reset()
        {
            State = FadeState.None;
        }
    }

    public static class Utils
    {
        private static readonly System.Diagnostics.Stopwatch _realClock = System.Diagnostics.Stopwatch.StartNew();

        public static readonly float[] ValidSpeeds = new float[]
        {
            0.10f, 0.25f, 0.50f, 0.75f, 1.00f, 1.25f, 1.50f, 1.75f, 2.00f, 2.50f, 3.00f, 4.00f, 5.00f, 10.00f
        };

        public static string[] SpeedLabels
        {
            get
            {
                string[] labels = new string[ValidSpeeds.Length];
                for (int i = 0; i < ValidSpeeds.Length; i++)
                    labels[i] = "x" + ValidSpeeds[i].ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                return labels;
            }
        }

        public static long NowMs()
        {
            return _realClock.ElapsedMilliseconds;
        }

        public static Vector3 RotationToDirection(Vector3 rotation)
        {
            double num = (double)(rotation.Z * 0.01745329f);
            double num2 = (double)(rotation.X * 0.01745329f);
            double num3 = Math.Abs(Math.Cos(num2));
            return new Vector3(
                (float)(-(float)(Math.Sin(num) * num3)),
                (float)(Math.Cos(num) * num3),
                (float)Math.Sin(num2));
        }

        public static Vector3 RightVector(this Vector3 position, Vector3 up)
        {
            position.Normalize();
            up.Normalize();
            return Vector3.Cross(position, up);
        }

        public static Vector3 LeftVector(this Vector3 position, Vector3 up)
        {
            position.Normalize();
            up.Normalize();
            return -Vector3.Cross(position, up);
        }
    }
}
