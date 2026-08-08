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
    public class Vector3JsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Vector3);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            Vector3 v = (Vector3)value;
            writer.WriteStartObject();
            writer.WritePropertyName("X");
            writer.WriteValue(v.X);
            writer.WritePropertyName("Y");
            writer.WriteValue(v.Y);
            writer.WritePropertyName("Z");
            writer.WriteValue(v.Z);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            float x = 0f, y = 0f, z = 0f;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject) break;
                if (reader.TokenType == JsonToken.PropertyName)
                {
                    string propName = (string)reader.Value;
                    reader.Read();
                    switch (propName)
                    {
                        case "X": x = (reader.Value != null) ? Convert.ToSingle(reader.Value) : 0f; break;
                        case "Y": y = (reader.Value != null) ? Convert.ToSingle(reader.Value) : 0f; break;
                        case "Z": z = (reader.Value != null) ? Convert.ToSingle(reader.Value) : 0f; break;
                    }
                }
            }
            return new Vector3(x, y, z);
        }
    }

    [Serializable]
    public class CameraPath
    {
        public string Name { get; set; }
        public int Version { get; set; }
        public List<Vector3> Positions { get; set; }
        public List<Vector3> Rotations { get; set; }
        public List<int> Durations { get; set; }
        public List<int> NodeInterpolationModes { get; set; }
        public List<int> NodeColors { get; set; }
        public int DefaultDuration { get; set; }
        public int Fov { get; set; }
        public float Speed { get; set; }
        public int InterpolationMode { get; set; }

        public CameraPath()
        {
            this.Positions = new List<Vector3>();
            this.Rotations = new List<Vector3>();
            this.Durations = new List<int>();
            this.NodeInterpolationModes = new List<int>();
            this.NodeColors = new List<int>();
            this.DefaultDuration = 5000;
            this.Fov = 50;
            this.Speed = 1.0f;
            this.InterpolationMode = 2;
        }

        public CameraPath(string name, List<Tuple<Vector3, Vector3>> nodes, List<int> nodeModes, int defaultDuration, int fov, float speed, int interpolationMode)
        {
            if (nodes == null) throw new ArgumentNullException("nodes", "Node list cannot be null");
            this.Name = name;
            this.Positions = new List<Vector3>();
            this.Rotations = new List<Vector3>();
            this.Durations = new List<int>();
            this.NodeInterpolationModes = new List<int>();
            this.NodeColors = new List<int>();
            this.DefaultDuration = defaultDuration;
            this.Fov = fov;
            this.Speed = speed;
            this.InterpolationMode = interpolationMode;
            int modeCount = (nodeModes != null) ? nodeModes.Count : 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                this.Positions.Add(nodes[i].Item1);
                this.Rotations.Add(nodes[i].Item2);
                this.Durations.Add(defaultDuration);
                this.NodeInterpolationModes.Add((i < modeCount) ? nodeModes[i] : interpolationMode);
            }
        }

        public CameraPath(string name, List<Vector3> positions, List<Vector3> rotations, List<int> durations, List<int> nodeModes, int defaultDuration, int fov, float speed, int interpolationMode)
        {
            this.Name = name;
            this.Positions = (positions != null) ? positions : new List<Vector3>();
            this.Rotations = (rotations != null) ? rotations : new List<Vector3>();
            this.Durations = (durations != null) ? durations : new List<int>();
            this.NodeInterpolationModes = (nodeModes != null) ? new List<int>(nodeModes) : new List<int>();
            this.NodeColors = new List<int>();
            this.DefaultDuration = defaultDuration;
            this.Fov = fov;
            this.Speed = speed;
            this.InterpolationMode = interpolationMode;
            // Ensure NodeInterpolationModes matches position count
            int count = (positions != null) ? positions.Count : 0;
            while (this.NodeInterpolationModes.Count < count)
                this.NodeInterpolationModes.Add(interpolationMode);
            if (this.NodeInterpolationModes.Count > count)
                this.NodeInterpolationModes.RemoveRange(count, this.NodeInterpolationModes.Count - count);
        }

        public List<Tuple<Vector3, Vector3>> ToNodes()
        {
            List<Tuple<Vector3, Vector3>> nodes = new List<Tuple<Vector3, Vector3>>();
            int count = Math.Min(this.Positions.Count, this.Rotations.Count);
            for (int i = 0; i < count; i++)
            {
                nodes.Add(new Tuple<Vector3, Vector3>(this.Positions[i], this.Rotations[i]));
            }
            return nodes;
        }

        public int GetNodeColor(int index)
        {
            if (this.NodeColors != null && index < this.NodeColors.Count)
                return this.NodeColors[index];
            return Color.White.ToArgb();
        }
    }

}
