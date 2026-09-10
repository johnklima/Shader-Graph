using UnityEngine;

namespace Synty.Tools
{
    [System.Serializable]
    public class AssetPackData
    {
        public string packName;
        public string displayName;
        public string imageUrl;
        public string storeUrl;
        public string shopifyVariantId; // Shopify variant ID for add-to-cart functionality
        public float price;             // Current price (e.g. 29.99)
        public string currency;         // Currency code (e.g. "USD")
        public int releaseOrder; // Lower number = newer/higher priority
        public string bucketFilename; // Current filename in the bucket (for version tracking)
        public string version;        // Latest available version from catalog (e.g. "1.0.6")
        public string releaseDate; // Release date string (e.g. "2025-03-15")
        public int iconVersion;    // Manual icon version — increment to force re-download
        
        [Header("Theme Filters")]
        public bool sciFi;
        public bool apocalypse;
        public bool horror;
        public bool fantasy;
        public bool pirates;
        public bool samurai;
        public bool vikings;
        public bool modern;
        public bool battle;
        public bool western;
        public bool biomes;
        public bool ancient;
        public bool other;
        
        [Header("Categories")]
        public PackCategory category;
        
        [Header("Ownership")]
        public bool notOwned;
        
        [Header("Sale")]
        public bool isOnSale;
        public string compareAtPrice; // Original price before sale (e.g. "39.99")
        
        [HideInInspector] public Texture2D loadedImage;
        [HideInInspector] public Texture2D greyscaleImage;
        [HideInInspector] public bool isLoading;
        
        /// <summary>
        /// Returns displayName if set, otherwise packName
        /// </summary>
        public string GetDisplayName()
        {
            return string.IsNullOrEmpty(displayName) ? packName : displayName;
        }
        
        /// <summary>
        /// Returns the primary theme for sorting purposes
        /// </summary>
        public string GetPrimaryTheme()
        {
            if (sciFi) return "Sci-Fi";
            if (apocalypse) return "Apocalypse";
            if (horror) return "Horror";
            if (fantasy) return "Fantasy";
            if (pirates) return "Pirates";
            if (samurai) return "Samurai";
            if (vikings) return "Vikings";
            if (modern) return "Modern";
            if (battle) return "Battle";
            if (biomes) return "Biomes";
            if (ancient) return "Ancient";
            if (other) return "Other";
            return "ZZZ"; // Sort to end if no theme
        }
    }
    
    public enum PackCategory
    {
        Polygon,
        Sidekicks,
        Interface,
        Animation,
        Simple
    }
}
