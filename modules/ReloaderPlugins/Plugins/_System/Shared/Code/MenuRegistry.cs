using System;
using System.Collections.Generic;
using LemonUI.Menus;

namespace ModPack.Core
{
    // Modules keep their own menus and state; hubs only route navigation.
    public static class MenuRegistry
    {
        private static readonly Dictionary<string, NativeMenu> Menus = new Dictionary<string, NativeMenu>(StringComparer.Ordinal);

        public static void Register(string id, NativeMenu menu)
        {
            if (string.IsNullOrEmpty(id) || menu == null) throw new ArgumentException("Menu registration is incomplete.");
            Menus[id] = menu;
        }

        public static NativeMenu Find(string id)
        {
            NativeMenu menu;
            return Menus.TryGetValue(id, out menu) ? menu : null;
        }

        public static void Unregister(string id, NativeMenu menu)
        {
            if (Find(id) == menu) Menus.Remove(id);
        }
    }
}
