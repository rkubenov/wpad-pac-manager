using System;
using System.Globalization;

namespace WpadManager.Core.Model
{
    // ISO-8601 UTC timestamp helper, used for created_at / updated_at across the model.
    public static class Iso
    {
        public static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
    }
}
