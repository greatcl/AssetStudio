using System.Windows.Forms;
using AssetStudio;

namespace AssetStudioGUI
{
    internal class AssetItem : ListViewItem
    {
        public Object Asset;
        public SerializedFile SourceFile;
        public string Container = string.Empty;
        public string TypeString;
        public long m_PathID;
        public long FullSize;
        public ClassIDType Type;
        public string InfoText;
        public string UniqueID;
        public GameObjectTreeNode TreeNode;

        public enum SizeUnit
        {
            Bytes,
            Human
        }

        public static SizeUnit CurrentSizeUnit = SizeUnit.Human;

        public static string FormatSize(long bytes)
        {
            return CurrentSizeUnit switch
            {
                SizeUnit.Bytes => bytes.ToString(),
                SizeUnit.Human or _ => bytes switch
                {
                    >= 1073741824 => (bytes / 1073741824.0).ToString("0.##") + " GB",
                    >= 1048576 => (bytes / 1048576.0).ToString("0.##") + " MB",
                    >= 1024 => (bytes / 1024.0).ToString("0.##") + " KB",
                    _ => bytes + " B"
                }
            };
        }

        public AssetItem(Object asset)
        {
            Asset = asset;
            SourceFile = asset.assetsFile;
            Type = asset.type;
            TypeString = Type.ToString();
            m_PathID = asset.m_PathID;
            FullSize = asset.byteSize;
        }

        public void SetSubItems()
        {
            SubItems.AddRange(new[]
            {
                Container, //Container
                TypeString, //Type
                m_PathID.ToString(), //PathID
                FormatSize(FullSize), //Size
            });
        }
    }
}
