namespace ModPack.Core
{
    // User-facing top-level menus. Keep the F5 order and labels here.
    public sealed class ModuleMenuInfo
    {
        public readonly string Id;
        public readonly string Title;
        public readonly string KeyLabel;
        public readonly string Description;

        public ModuleMenuInfo(string id, string title, string keyLabel, string description)
        {
            Id = id;
            Title = title;
            KeyLabel = keyLabel;
            Description = description;
        }
    }

    public static class ModuleMenus
    {
        public static readonly ModuleMenuInfo Camera = new ModuleMenuInfo(
            "Camera", "Камера", "K", "Пролётки и сохранённые пути");
        public static readonly ModuleMenuInfo Presets = new ModuleMenuInfo(
            "Presets", "Пресеты", "T", "Сохранённые сценарии массовых действий");
        public static readonly ModuleMenuInfo Optimization = new ModuleMenuInfo(
            "Optimization", "Оптимизация", "O", "Загрузка, очистка и управление NPC");
        public static readonly ModuleMenuInfo VehiclePaint = new ModuleMenuInfo(
            "VehiclePaint", "Покраска машины", "I", "Цвета выбранной машины");
        public static readonly ModuleMenuInfo Misc = new ModuleMenuInfo(
            "Misc", "Разное", "L", "Оружие, физика, животные, камера и обмен машин");
        public static readonly ModuleMenuInfo Animations = new ModuleMenuInfo(
            "Animations", "Анимации педов", "Y", "Анимации выбранных педов в Object Spooner");
        public static readonly ModuleMenuInfo Prefabs = new ModuleMenuInfo(
            "Prefabs", "Заготовки", ";", "Стенка из мячей и горка в Object Spooner");
        public static readonly ModuleMenuInfo SlowMo = new ModuleMenuInfo(
            "SlowMo", "Замедление", "Mouse4", "Замедление и камера при удержании");

        public static readonly ModuleMenuInfo[] InCatalogOrder = {
            Camera, Presets, Optimization, VehiclePaint, Misc, Animations, Prefabs, SlowMo
        };
    }
}
