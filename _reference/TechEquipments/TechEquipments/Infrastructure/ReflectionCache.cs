using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace TechEquipments
{
    /// <summary>
    /// Кеш reflection спеціально для Param-моделей:
    /// - тільки public instance properties
    /// - тільки ті, що мають get+set
    /// - без індексаторів
    /// - без службових полів (Unit/Chanel)
    /// - з детермінованим порядком (MetadataToken)
    /// </summary>
    public static class ReflectionCache
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _paramPropsCache = new();

        public static PropertyInfo[] GetParamProperties(Type type)
        {
            return _paramPropsCache.GetOrAdd(type, t =>
            {
                var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);

                return props
                    .Where(p => p.CanRead && p.CanWrite)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    // службові поля не читаємо з SCADA:
                    .Where(p =>
                        !p.Name.Equals("Unit", StringComparison.OrdinalIgnoreCase) &&
                        !p.Name.Equals("Chanel", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.MetadataToken)
                    .ToArray();
            });
        }
    }
}