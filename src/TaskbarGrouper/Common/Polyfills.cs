namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace StackBar.Common
{
    public static class Polyfills
    {
        public static int Clamp(int value, int min, int max)
        {
            if (min > max) throw new System.ArgumentException("min cannot be greater than max");
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (min > max) throw new System.ArgumentException("min cannot be greater than max");
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static bool ContainsIgnoreCase(this string source, string toCheck)
        {
            if (source == null || toCheck == null) return false;
            return source.IndexOf(toCheck, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool EqualsIgnoreCase(this string a, string b)
        {
            return string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
