using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AssetStudio;
using Object = AssetStudio.Object;

namespace AssetStudioGUI
{
    internal enum CheckSeverity
    {
        Error,
        Warning,
        Info
    }

    internal enum CheckCategory
    {
        MissingAtlasRef,
        MissingAtlasImage,
        UnusedAtlasImage,
        AtlasSpaceWaste
    }

    internal enum CheckScope
    {
        WholeBundle,
        SelectedNode
    }

    internal sealed class CheckIssue
    {
        public CheckSeverity Severity;
        public CheckCategory Category;
        public string Source = string.Empty;
        public string Target = string.Empty;
        public string Message = string.Empty;
        public string NodePath = string.Empty;
        public TreeNode TreeNode;
        public Object Asset;

        /// <summary>
        /// For <see cref="CheckCategory.AtlasSpaceWaste"/>: the in-scope sprite rects on the atlas page,
        /// in atlas pixel space (origin bottom-left). Lets the preview outline what is and is not claimed.
        /// </summary>
        public List<RectangleF> UsedRects;
    }

    internal static class ResourceChecker
    {
        // Pages smaller than this are cheap enough that low utilization is not worth reporting.
        private const long MinAtlasArea = 128 * 128;
        private const double LowUtilizationThreshold = 0.6;
        private const long MinWastedBytes = 32 * 1024;
        // Gaps thinner than this are packing padding, not reclaimable space; outlining them only adds noise.
        private const float MinFreeRectSide = 16f;

        private static readonly HashSet<string> SpineSkeletonUsers = new HashSet<string>(StringComparer.Ordinal)
        {
            "SkeletonGraphic",
            "SkeletonAnimation",
            "SkeletonRenderer",
            "SkeletonMecanim"
        };

        public static List<CheckIssue> Run(IEnumerable<AssetItem> assetItems, CheckScope scope, TreeNode selectedNode)
        {
            var issues = new List<CheckIssue>();
            var items = assetItems?.ToList() ?? new List<AssetItem>();
            var itemByObject = new Dictionary<Object, AssetItem>();
            foreach (var item in items)
            {
                if (item?.Asset != null && !itemByObject.ContainsKey(item.Asset))
                    itemByObject[item.Asset] = item;
            }

            var scopeNodes = BuildScopeNodes(scope, selectedNode);
            if (scope == CheckScope.SelectedNode && (scopeNodes == null || scopeNodes.Count == 0))
            {
                issues.Add(new CheckIssue
                {
                    Severity = CheckSeverity.Info,
                    Category = CheckCategory.MissingAtlasRef,
                    Source = "Check",
                    Message = "Selected Node scope requires a Scene Hierarchy selection."
                });
                return issues;
            }

            var referencedTextures = new HashSet<Object>();
            var referencedSprites = new HashSet<Object>();
            CollectReferencedAssets(items, scope, scopeNodes, referencedTextures, referencedSprites);

            var spineParseFailed = false;
            foreach (var assetsFile in Studio.assetsManager.AssetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    switch (obj)
                    {
                        case MonoBehaviour mb:
                            CheckSpineMonoBehaviour(mb, itemByObject, issues, ref spineParseFailed);
                            break;
                        case Sprite sprite:
                            CheckSpriteAtlasRef(sprite, itemByObject, issues);
                            break;
                        case SpriteAtlas spriteAtlas:
                            CheckSpriteAtlasImages(spriteAtlas, itemByObject, issues);
                            CheckUnusedSpriteAtlas(spriteAtlas, itemByObject, referencedSprites, referencedTextures, scope, issues);
                            break;
                        case Object o when o.type == ClassIDType.SpriteRenderer:
                            CheckSpriteRendererRef(o, itemByObject, issues);
                            break;
                    }
                }
            }

            // Spine unused + atlas text page checks need a second pass over identified atlas assets
            foreach (var assetsFile in Studio.assetsManager.AssetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is MonoBehaviour mb && TryGetScriptClassName(mb, out var className) && className == "SpineAtlasAsset")
                    {
                        CheckSpineAtlasAsset(mb, itemByObject, referencedTextures, scope, issues);
                    }
                    else if (obj is MonoBehaviour mbImage && TryGetScriptClassName(mbImage, out var uiClass) &&
                             (uiClass == "Image" || uiClass == "RawImage"))
                    {
                        CheckUiImageRef(mbImage, uiClass, itemByObject, issues);
                    }
                }
            }

            CheckAtlasPageUsage(itemByObject, referencedSprites, scope, issues);

            if (spineParseFailed)
            {
                issues.Insert(0, new CheckIssue
                {
                    Severity = CheckSeverity.Info,
                    Category = CheckCategory.MissingAtlasRef,
                    Source = "Spine",
                    Message = "Some Spine MonoBehaviour fields could not be parsed (missing TypeTree/assembly). Related checks may be incomplete."
                });
            }

            return issues;
        }

        private static HashSet<TreeNode> BuildScopeNodes(CheckScope scope, TreeNode selectedNode)
        {
            if (scope != CheckScope.SelectedNode || selectedNode == null)
                return null;

            var set = new HashSet<TreeNode>();
            CollectSubtree(selectedNode, set);
            return set;
        }

        private static void CollectSubtree(TreeNode node, HashSet<TreeNode> set)
        {
            if (node == null || !set.Add(node))
                return;
            foreach (TreeNode child in node.Nodes)
                CollectSubtree(child, set);
        }

        private static void CollectReferencedAssets(List<AssetItem> items, CheckScope scope, HashSet<TreeNode> scopeNodes,
            HashSet<Object> referencedTextures, HashSet<Object> referencedSprites)
        {
            foreach (var item in items)
            {
                if (item?.Asset == null || item.TreeNode == null)
                    continue;

                if (scope == CheckScope.WholeBundle)
                {
                    // Prefab-linked only; PreloadTable orphans hang on the file root TreeNode
                    if (!(item.TreeNode is GameObjectTreeNode))
                        continue;
                }
                else if (scopeNodes == null || !scopeNodes.Contains(item.TreeNode))
                {
                    continue;
                }

                switch (item.Asset)
                {
                    case Texture2D _:
                        referencedTextures.Add(item.Asset);
                        break;
                    case Sprite _:
                        referencedSprites.Add(item.Asset);
                        break;
                }
            }

            if (scope != CheckScope.SelectedNode || scopeNodes == null)
                return;

            // Re-scan components under the selected subtree to catch Spine chains even if TreeNode linking missed something
            foreach (var node in scopeNodes)
            {
                if (!(node is GameObjectTreeNode goNode) || goNode.gameObject == null)
                    continue;
                foreach (var pptr in goNode.gameObject.m_Components)
                {
                    if (!pptr.TryGet(out Component component))
                        continue;
                    CollectObjectReferences(component, referencedTextures, referencedSprites, new HashSet<Object>());
                }
            }
        }

        private static void CollectObjectReferences(Object obj, HashSet<Object> referencedTextures, HashSet<Object> referencedSprites,
            HashSet<Object> visited)
        {
            if (obj == null || !visited.Add(obj))
                return;

            switch (obj)
            {
                case Texture2D _:
                    referencedTextures.Add(obj);
                    return;
                case Sprite sprite:
                    referencedSprites.Add(sprite);
                    if (sprite.m_RD?.texture != null && sprite.m_RD.texture.TryGet(out Texture2D tex))
                        referencedTextures.Add(tex);
                    return;
                case Material material:
                    CollectMaterialTextures(material, referencedTextures);
                    return;
                case GameObject _:
                case Transform _:
                    return;
                case MonoBehaviour mb when !mb.m_GameObject.IsNull && !IsSpineDataAsset(mb):
                    // attached component: still scan its fields
                    break;
                case Component _ when !(obj is MonoBehaviour):
                    // SpriteRenderer etc. handled via ToType below
                    break;
            }

            try
            {
                var typeDict = obj.ToType();
                if (typeDict != null)
                    WalkPPtrs(typeDict, referencedTextures, referencedSprites, visited, obj.assetsFile);
            }
            catch
            {
                // ignore
            }
        }

        private static bool IsSpineDataAsset(MonoBehaviour mb)
        {
            return TryGetScriptClassName(mb, out var name) &&
                   (name == "SpineAtlasAsset" || name == "SkeletonDataAsset");
        }

        private static void CollectMaterialTextures(Material material, HashSet<Object> referencedTextures)
        {
            if (material.m_SavedProperties?.m_TexEnvs == null)
                return;
            foreach (var texEnv in material.m_SavedProperties.m_TexEnvs)
            {
                if (texEnv.Value?.m_Texture != null && texEnv.Value.m_Texture.TryGet(out Texture2D tex))
                    referencedTextures.Add(tex);
            }
        }

        private static void WalkPPtrs(object value, HashSet<Object> referencedTextures, HashSet<Object> referencedSprites,
            HashSet<Object> visited, SerializedFile assetsFile)
        {
            if (value == null)
                return;

            if (value is OrderedDictionary dict)
            {
                if (TryReadPPtr(dict, out var fileId, out var pathId) && fileId == 0 && pathId != 0)
                {
                    if (assetsFile.ObjectsDic.TryGetValue(pathId, out var referenced))
                    {
                        if (referenced is GameObject || (referenced is Component c && !(referenced is MonoBehaviour mb && mb.m_GameObject.IsNull)))
                        {
                            // skip scene graph
                        }
                        else
                        {
                            CollectObjectReferences(referenced, referencedTextures, referencedSprites, visited);
                        }
                    }
                }

                foreach (DictionaryEntry entry in dict)
                    WalkPPtrs(entry.Value, referencedTextures, referencedSprites, visited, assetsFile);
            }
            else if (value is IEnumerable enumerable && !(value is string))
            {
                foreach (var item in enumerable)
                    WalkPPtrs(item, referencedTextures, referencedSprites, visited, assetsFile);
            }
        }

        private static void CheckSpineMonoBehaviour(MonoBehaviour mb, Dictionary<Object, AssetItem> itemByObject,
            List<CheckIssue> issues, ref bool spineParseFailed)
        {
            if (!TryGetScriptClassName(mb, out var className))
                return;

            OrderedDictionary typeDict = null;
            try
            {
                typeDict = mb.ToType();
            }
            catch
            {
                spineParseFailed = true;
                return;
            }

            if (typeDict == null)
            {
                if (SpineSkeletonUsers.Contains(className) || className == "SkeletonDataAsset" || className == "SpineAtlasAsset")
                    spineParseFailed = true;
                return;
            }

            var sourceName = GetObjectName(mb);
            var nodePath = GetNodePath(mb, itemByObject);
            var treeNode = GetTreeNode(mb, itemByObject);

            if (SpineSkeletonUsers.Contains(className))
            {
                if (!TryResolveFieldPPtr(typeDict, mb.assetsFile, "skeletonDataAsset", out Object _) &&
                    HasField(typeDict, "skeletonDataAsset"))
                {
                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Error,
                        Category = CheckCategory.MissingAtlasRef,
                        Source = $"{className} ({sourceName})",
                        Target = "skeletonDataAsset",
                        Message = "skeletonDataAsset reference is missing.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = mb
                    });
                }
            }
            else if (className == "SkeletonDataAsset")
            {
                foreach (var (label, resolved) in EnumerateArrayPPtrs(typeDict, mb.assetsFile, "atlasAssets"))
                {
                    if (!resolved)
                    {
                        issues.Add(new CheckIssue
                        {
                            Severity = CheckSeverity.Error,
                            Category = CheckCategory.MissingAtlasRef,
                            Source = $"SkeletonDataAsset ({sourceName})",
                            Target = label,
                            Message = "atlasAssets entry reference is missing.",
                            NodePath = nodePath,
                            TreeNode = treeNode,
                            Asset = mb
                        });
                    }
                }
            }
            else if (className == "SpineAtlasAsset")
            {
                if (HasField(typeDict, "atlasFile") &&
                    !TryResolveFieldPPtr(typeDict, mb.assetsFile, "atlasFile", out Object _) &&
                    TryReadFieldPPtr(typeDict, "atlasFile", out _, out var atlasPathId) && atlasPathId != 0)
                {
                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Error,
                        Category = CheckCategory.MissingAtlasRef,
                        Source = $"SpineAtlasAsset ({sourceName})",
                        Target = $"atlasFile PathID {atlasPathId}",
                        Message = "atlasFile (TextAsset) reference is missing.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = mb
                    });
                }

                foreach (var (label, resolved) in EnumerateArrayPPtrs(typeDict, mb.assetsFile, "materials"))
                {
                    if (!resolved)
                    {
                        issues.Add(new CheckIssue
                        {
                            Severity = CheckSeverity.Error,
                            Category = CheckCategory.MissingAtlasRef,
                            Source = $"SpineAtlasAsset ({sourceName})",
                            Target = label,
                            Message = "materials entry reference is missing.",
                            NodePath = nodePath,
                            TreeNode = treeNode,
                            Asset = mb
                        });
                    }
                }
            }
        }

        private static void CheckSpineAtlasAsset(MonoBehaviour atlasMb, Dictionary<Object, AssetItem> itemByObject,
            HashSet<Object> referencedTextures, CheckScope scope, List<CheckIssue> issues)
        {
            var sourceName = GetObjectName(atlasMb);
            var nodePath = GetNodePath(atlasMb, itemByObject);
            var treeNode = GetTreeNode(atlasMb, itemByObject);
            var materialTextures = new List<Texture2D>();
            var typeDict = SafeToType(atlasMb);

            if (typeDict != null)
            {
                foreach (var mat in ResolveArrayObjects(typeDict, atlasMb.assetsFile, "materials"))
                {
                    if (!(mat is Material material))
                        continue;

                    if (material.m_SavedProperties?.m_TexEnvs == null)
                        continue;

                    foreach (var texEnv in material.m_SavedProperties.m_TexEnvs)
                    {
                        var slot = texEnv.Key ?? "texture";
                        if (texEnv.Value?.m_Texture == null || texEnv.Value.m_Texture.IsNull)
                            continue;

                        if (!texEnv.Value.m_Texture.TryGet(out Texture2D tex))
                        {
                            issues.Add(new CheckIssue
                            {
                                Severity = CheckSeverity.Error,
                                Category = CheckCategory.MissingAtlasImage,
                                Source = $"SpineAtlasAsset ({sourceName})",
                                Target = $"{GetObjectName(material)}.{slot}",
                                Message = "Material texture reference is missing.",
                                NodePath = nodePath,
                                TreeNode = treeNode,
                                Asset = atlasMb
                            });
                        }
                        else
                        {
                            materialTextures.Add(tex);
                        }
                    }
                }
            }

            // Match .atlas TextAsset pages against material textures
            var atlasText = FindRelatedAtlasText(atlasMb);
            if (atlasText != null)
            {
                var pages = ParseAtlasPages(Encoding.UTF8.GetString(atlasText.m_Script));
                var texNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tex in materialTextures)
                {
                    texNames.Add(tex.m_Name);
                    texNames.Add(Path.GetFileNameWithoutExtension(tex.m_Name));
                }

                foreach (var page in pages)
                {
                    var pageBase = Path.GetFileNameWithoutExtension(page);
                    if (texNames.Contains(page) || texNames.Contains(pageBase))
                        continue;

                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Error,
                        Category = CheckCategory.MissingAtlasImage,
                        Source = $"SpineAtlasAsset ({sourceName})",
                        Target = page,
                        Message = $".atlas page \"{page}\" has no matching Material texture.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = atlasText
                    });
                }
            }

            // Unused page textures
            foreach (var tex in materialTextures.Distinct())
            {
                if (referencedTextures.Contains(tex))
                    continue;

                issues.Add(new CheckIssue
                {
                    Severity = CheckSeverity.Warning,
                    Category = CheckCategory.UnusedAtlasImage,
                    Source = $"SpineAtlasAsset ({sourceName})",
                    Target = tex.m_Name,
                    Message = scope == CheckScope.SelectedNode
                        ? "Atlas texture is not referenced by the selected node (Prefab root) subtree."
                        : "Atlas texture is not referenced by any Prefab/GameObject in the loaded files.",
                    NodePath = nodePath,
                    TreeNode = treeNode,
                    Asset = tex
                });
            }
        }

        private static void CheckSpriteAtlasRef(Sprite sprite, Dictionary<Object, AssetItem> itemByObject, List<CheckIssue> issues)
        {
            if (sprite.m_SpriteAtlas == null || sprite.m_SpriteAtlas.IsNull)
                return;
            if (sprite.m_SpriteAtlas.TryGet(out SpriteAtlas _))
                return;

            issues.Add(new CheckIssue
            {
                Severity = CheckSeverity.Error,
                Category = CheckCategory.MissingAtlasRef,
                Source = $"Sprite ({sprite.m_Name})",
                Target = $"PathID {sprite.m_SpriteAtlas.m_PathID}",
                Message = "Sprite.m_SpriteAtlas reference is missing.",
                NodePath = GetNodePath(sprite, itemByObject),
                TreeNode = GetTreeNode(sprite, itemByObject),
                Asset = sprite
            });
        }

        private static void CheckSpriteAtlasImages(SpriteAtlas atlas, Dictionary<Object, AssetItem> itemByObject, List<CheckIssue> issues)
        {
            var sourceName = atlas.m_Name;
            var nodePath = GetNodePath(atlas, itemByObject);
            var treeNode = GetTreeNode(atlas, itemByObject);

            if (atlas.m_PackedSprites != null)
            {
                for (var i = 0; i < atlas.m_PackedSprites.Count; i++)
                {
                    var pptr = atlas.m_PackedSprites[i];
                    if (pptr == null || pptr.IsNull)
                        continue;
                    if (pptr.TryGet(out Sprite _))
                        continue;

                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Error,
                        Category = CheckCategory.MissingAtlasImage,
                        Source = $"SpriteAtlas ({sourceName})",
                        Target = $"m_PackedSprites[{i}] PathID {pptr.m_PathID}",
                        Message = "Packed sprite reference is missing.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = atlas
                    });
                }
            }

            if (atlas.m_RenderDataMap == null)
                return;

            foreach (var kv in atlas.m_RenderDataMap)
            {
                var data = kv.Value;
                if (data == null)
                    continue;

                if (data.texture != null && !data.texture.IsNull && !data.texture.TryGet(out Texture2D _))
                {
                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Error,
                        Category = CheckCategory.MissingAtlasImage,
                        Source = $"SpriteAtlas ({sourceName})",
                        Target = $"texture PathID {data.texture.m_PathID}",
                        Message = "RenderDataMap texture reference is missing.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = atlas
                    });
                }

                if (data.alphaTexture != null && !data.alphaTexture.IsNull && !data.alphaTexture.TryGet(out Texture2D _))
                {
                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Error,
                        Category = CheckCategory.MissingAtlasImage,
                        Source = $"SpriteAtlas ({sourceName})",
                        Target = $"alphaTexture PathID {data.alphaTexture.m_PathID}",
                        Message = "RenderDataMap alphaTexture reference is missing.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = atlas
                    });
                }
            }
        }

        private static void CheckUnusedSpriteAtlas(SpriteAtlas atlas, Dictionary<Object, AssetItem> itemByObject,
            HashSet<Object> referencedSprites, HashSet<Object> referencedTextures, CheckScope scope, List<CheckIssue> issues)
        {
            var sourceName = atlas.m_Name;
            var nodePath = GetNodePath(atlas, itemByObject);
            var treeNode = GetTreeNode(atlas, itemByObject);
            var scopeLabel = scope == CheckScope.SelectedNode
                ? "selected node (Prefab root) subtree"
                : "any Prefab/GameObject in the loaded files";

            if (atlas.m_PackedSprites != null)
            {
                foreach (var pptr in atlas.m_PackedSprites)
                {
                    if (pptr == null || !pptr.TryGet(out Sprite sprite))
                        continue;
                    if (referencedSprites.Contains(sprite))
                        continue;

                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Warning,
                        Category = CheckCategory.UnusedAtlasImage,
                        Source = $"SpriteAtlas ({sourceName})",
                        Target = sprite.m_Name,
                        Message = $"Packed sprite is not referenced by the {scopeLabel}.",
                        NodePath = nodePath,
                        TreeNode = treeNode,
                        Asset = sprite
                    });
                }
            }

            if (atlas.m_RenderDataMap == null)
                return;

            var reported = new HashSet<Object>();
            foreach (var data in atlas.m_RenderDataMap.Values)
            {
                if (data?.texture == null || !data.texture.TryGet(out Texture2D tex))
                    continue;
                if (!reported.Add(tex))
                    continue;
                if (referencedTextures.Contains(tex))
                    continue;

                // If any packed sprite from this atlas is referenced and uses this page, skip
                // (page may still be needed). Only warn when page texture itself is unreferenced.
                issues.Add(new CheckIssue
                {
                    Severity = CheckSeverity.Warning,
                    Category = CheckCategory.UnusedAtlasImage,
                    Source = $"SpriteAtlas ({sourceName})",
                    Target = tex.m_Name,
                    Message = $"Atlas page texture is not referenced by the {scopeLabel}.",
                    NodePath = nodePath,
                    TreeNode = treeNode,
                    Asset = tex
                });
            }
        }

        /// <summary>
        /// Covers atlases that are plain packed textures rather than Unity SpriteAtlas assets: reports sprites
        /// nothing references, and pages whose in-scope sprites only claim a small part of the payload.
        /// </summary>
        private static void CheckAtlasPageUsage(Dictionary<Object, AssetItem> itemByObject,
            HashSet<Object> referencedSprites, CheckScope scope, List<CheckIssue> issues)
        {
            var spritesByPage = new Dictionary<Texture2D, List<Sprite>>();
            foreach (var assetsFile in Studio.assetsManager.AssetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (!(obj is Sprite sprite) || sprite.m_RD?.texture == null)
                        continue;
                    if (!sprite.m_RD.texture.TryGet(out Texture2D page))
                        continue;
                    if (!spritesByPage.TryGetValue(page, out var list))
                        spritesByPage[page] = list = new List<Sprite>();
                    list.Add(sprite);
                }
            }

            var scopeLabel = scope == CheckScope.SelectedNode
                ? "the selected node (Prefab root) subtree"
                : "any Prefab/GameObject in the loaded files";

            foreach (var kv in spritesByPage)
            {
                var page = kv.Key;
                var pageSprites = kv.Value;
                var pageName = string.IsNullOrEmpty(page.m_Name) ? $"PathID {page.m_PathID}" : page.m_Name;
                var source = $"Atlas page ({pageName}) {page.m_Width}x{page.m_Height}";
                var nodePath = GetNodePath(page, itemByObject);
                var treeNode = GetTreeNode(page, itemByObject);

                foreach (var sprite in pageSprites)
                {
                    if (referencedSprites.Contains(sprite))
                        continue;
                    // SpriteAtlas members are already covered by CheckUnusedSpriteAtlas
                    if (sprite.m_SpriteAtlas != null && sprite.m_SpriteAtlas.TryGet(out SpriteAtlas _))
                        continue;

                    issues.Add(new CheckIssue
                    {
                        Severity = CheckSeverity.Warning,
                        Category = CheckCategory.UnusedAtlasImage,
                        Source = source,
                        Target = string.IsNullOrEmpty(sprite.m_Name) ? $"PathID {sprite.m_PathID}" : sprite.m_Name,
                        Message = $"Sprite is not referenced by {scopeLabel}.",
                        NodePath = GetNodePath(sprite, itemByObject),
                        TreeNode = GetTreeNode(sprite, itemByObject),
                        Asset = sprite
                    });
                }

                var atlasArea = (long)page.m_Width * page.m_Height;
                if (atlasArea < MinAtlasArea)
                    continue;

                var inScope = pageSprites.Where(referencedSprites.Contains).ToList();
                var usedRects = ClampRects(inScope.Select(s => s.m_RD.textureRect), page.m_Width, page.m_Height);
                var usedArea = UnionArea(usedRects);
                var ratio = (double)usedArea / atlasArea;

                var payload = GetTextureByteSize(page);
                var companion = FindAlphaCompanion(page, pageSprites);
                if (companion != null)
                    payload += GetTextureByteSize(companion);

                var wasted = (long)(payload * (1.0 - ratio));
                if (ratio >= LowUtilizationThreshold || wasted < MinWastedBytes)
                    continue;

                var companionNote = companion != null
                    ? $" Payload includes the alpha companion \"{companion.m_Name}\"."
                    : string.Empty;

                string message;
                if (inScope.Count == 0)
                {
                    message = $"None of the {pageSprites.Count} sprite(s) on this page is used by {scopeLabel}, " +
                              $"so its whole {FormatSize(payload)} payload is dead weight here.{companionNote}";
                }
                else
                {
                    var orphanCount = pageSprites.Count - inScope.Count;
                    var orphanNote = orphanCount > 0
                        ? $" {orphanCount} further sprite(s) sit on this page unused."
                        : string.Empty;
                    message = $"{inScope.Count} sprite(s) used by {scopeLabel} claim only {ratio * 100:F1}% of this page, " +
                              $"so roughly {FormatSize(wasted)} of its {FormatSize(payload)} payload is dead weight. " +
                              $"Repacking them into a dedicated atlas would reclaim it.{orphanNote}{companionNote}";
                }

                issues.Add(new CheckIssue
                {
                    Severity = CheckSeverity.Warning,
                    Category = CheckCategory.AtlasSpaceWaste,
                    Source = source,
                    Target = $"{ratio * 100:F1}% used",
                    Message = message,
                    NodePath = nodePath,
                    TreeNode = treeNode,
                    Asset = page,
                    UsedRects = usedRects
                });
            }
        }

        private static long GetTextureByteSize(Texture2D tex)
        {
            if (tex.m_StreamData != null && tex.m_StreamData.size > 0)
                return (long)tex.m_StreamData.size;
            return tex.m_CompleteImageSize;
        }

        /// <summary>
        /// ETC1-style packing splits opacity into a sibling texture, so the wasted payload is double-counted
        /// unless the companion is folded in.
        /// </summary>
        private static Texture2D FindAlphaCompanion(Texture2D page, List<Sprite> pageSprites)
        {
            foreach (var sprite in pageSprites)
            {
                if (sprite.m_RD?.alphaTexture != null && sprite.m_RD.alphaTexture.TryGet(out Texture2D alpha) && alpha != page)
                    return alpha;
            }

            if (string.IsNullOrEmpty(page.m_Name))
                return null;

            var candidates = new List<string>();
            if (page.m_Name.EndsWith("_rgb", StringComparison.OrdinalIgnoreCase))
                candidates.Add(page.m_Name.Substring(0, page.m_Name.Length - 4) + "_alpha");
            candidates.Add(page.m_Name + "_alpha");

            foreach (var obj in page.assetsFile.Objects)
            {
                if (!(obj is Texture2D tex) || tex == page || string.IsNullOrEmpty(tex.m_Name))
                    continue;
                foreach (var candidate in candidates)
                {
                    if (string.Equals(candidate, tex.m_Name, StringComparison.OrdinalIgnoreCase))
                        return tex;
                }
            }
            return null;
        }

        private static List<RectangleF> ClampRects(IEnumerable<Rectf> rects, int width, int height)
        {
            var boxes = new List<RectangleF>();
            foreach (var r in rects)
            {
                if (r == null)
                    continue;
                var x0 = Math.Max(0f, r.x);
                var y0 = Math.Max(0f, r.y);
                var x1 = Math.Min(width, r.x + r.width);
                var y1 = Math.Min(height, r.y + r.height);
                if (x1 > x0 && y1 > y0)
                    boxes.Add(new RectangleF(x0, y0, x1 - x0, y1 - y0));
            }
            return boxes;
        }

        /// <summary>
        /// Exact area of the union of the rects via a vertical sweep, so overlapping or duplicated
        /// sprite rects are not counted twice.
        /// </summary>
        private static long UnionArea(List<RectangleF> boxes)
        {
            if (boxes.Count == 0)
                return 0;

            var xEdges = boxes.Select(b => b.Left).Concat(boxes.Select(b => b.Right)).Distinct().OrderBy(v => v).ToArray();
            var spans = new List<(float Lo, float Hi)>();
            double area = 0;

            for (var i = 0; i + 1 < xEdges.Length; i++)
            {
                float left = xEdges[i], right = xEdges[i + 1];
                var width = right - left;
                if (width <= 0)
                    continue;

                spans.Clear();
                foreach (var b in boxes)
                {
                    if (b.Left <= left && b.Right >= right)
                        spans.Add((b.Top, b.Bottom));
                }
                if (spans.Count == 0)
                    continue;

                spans.Sort((a, b) => a.Lo.CompareTo(b.Lo));
                double covered = 0;
                float curLo = spans[0].Lo, curHi = spans[0].Hi;
                for (var s = 1; s < spans.Count; s++)
                {
                    if (spans[s].Lo > curHi)
                    {
                        covered += curHi - curLo;
                        curLo = spans[s].Lo;
                        curHi = spans[s].Hi;
                    }
                    else if (spans[s].Hi > curHi)
                    {
                        curHi = spans[s].Hi;
                    }
                }
                covered += curHi - curLo;
                area += covered * width;
            }

            return (long)Math.Round(area);
        }

        /// <summary>
        /// Decomposes the part of the page that no in-scope sprite claims into a small set of rectangles,
        /// so the preview can outline the reclaimable space. Coordinates are atlas space (origin bottom-left).
        /// </summary>
        internal static List<RectangleF> ComputeFreeRects(List<RectangleF> usedRects, int width, int height,
            float minSide = MinFreeRectSide)
        {
            var result = new List<RectangleF>();
            if (width <= 0 || height <= 0)
                return result;

            var boxes = usedRects ?? new List<RectangleF>();
            var xs = BuildEdges(boxes.Select(b => b.Left).Concat(boxes.Select(b => b.Right)), width);
            var ys = BuildEdges(boxes.Select(b => b.Top).Concat(boxes.Select(b => b.Bottom)), height);
            int nx = xs.Length - 1, ny = ys.Length - 1;
            if (nx <= 0 || ny <= 0)
                return result;

            var blocked = new bool[nx, ny];
            for (var i = 0; i < nx; i++)
            {
                var cx = (xs[i] + xs[i + 1]) / 2f;
                for (var j = 0; j < ny; j++)
                {
                    var cy = (ys[j] + ys[j + 1]) / 2f;
                    foreach (var b in boxes)
                    {
                        if (cx >= b.Left && cx <= b.Right && cy >= b.Top && cy <= b.Bottom)
                        {
                            blocked[i, j] = true;
                            break;
                        }
                    }
                }
            }

            // Either sweep direction is valid but they group very differently depending on how the
            // sprites happen to be packed, so take whichever yields the tidier decomposition.
            var rowFirst = Decompose(blocked, xs, ys, nx, ny, minSide, true);
            var columnFirst = Decompose(blocked, xs, ys, nx, ny, minSide, false);
            return columnFirst.Count < rowFirst.Count ? columnFirst : rowFirst;
        }

        /// <summary>
        /// Greedy maximal rectangles over the free cells of the compressed grid. When <paramref name="rowFirst"/>
        /// is set each seed grows right then down, otherwise it grows down then right.
        /// </summary>
        private static List<RectangleF> Decompose(bool[,] blocked, float[] xs, float[] ys, int nx, int ny,
            float minSide, bool rowFirst)
        {
            var result = new List<RectangleF>();
            var consumed = new bool[nx, ny];

            int outerCount = rowFirst ? ny : nx;
            int innerCount = rowFirst ? nx : ny;

            for (var outer = 0; outer < outerCount; outer++)
            {
                for (var inner = 0; inner < innerCount; inner++)
                {
                    int i = rowFirst ? inner : outer;
                    int j = rowFirst ? outer : inner;
                    if (blocked[i, j] || consumed[i, j])
                        continue;

                    int iEnd = i, jEnd = j;
                    if (rowFirst)
                    {
                        while (iEnd + 1 < nx && IsFree(blocked, consumed, iEnd + 1, j))
                            iEnd++;
                        while (jEnd + 1 < ny && IsRowFree(blocked, consumed, i, iEnd, jEnd + 1))
                            jEnd++;
                    }
                    else
                    {
                        while (jEnd + 1 < ny && IsFree(blocked, consumed, i, jEnd + 1))
                            jEnd++;
                        while (iEnd + 1 < nx && IsColumnFree(blocked, consumed, iEnd + 1, j, jEnd))
                            iEnd++;
                    }

                    for (var a = i; a <= iEnd; a++)
                        for (var b = j; b <= jEnd; b++)
                            consumed[a, b] = true;

                    float rx = xs[i], ry = ys[j];
                    float rw = xs[iEnd + 1] - rx, rh = ys[jEnd + 1] - ry;
                    if (rw >= minSide && rh >= minSide)
                        result.Add(new RectangleF(rx, ry, rw, rh));

                    inner = rowFirst ? iEnd : jEnd;
                }
            }
            return result;
        }

        private static bool IsFree(bool[,] blocked, bool[,] consumed, int i, int j)
        {
            return !blocked[i, j] && !consumed[i, j];
        }

        private static bool IsRowFree(bool[,] blocked, bool[,] consumed, int from, int to, int row)
        {
            for (var i = from; i <= to; i++)
            {
                if (!IsFree(blocked, consumed, i, row))
                    return false;
            }
            return true;
        }

        private static bool IsColumnFree(bool[,] blocked, bool[,] consumed, int column, int from, int to)
        {
            for (var j = from; j <= to; j++)
            {
                if (!IsFree(blocked, consumed, column, j))
                    return false;
            }
            return true;
        }

        private static float[] BuildEdges(IEnumerable<float> values, int limit)
        {
            var set = new SortedSet<float> { 0f, limit };
            foreach (var v in values)
            {
                if (v > 0f && v < limit)
                    set.Add(v);
            }
            return set.ToArray();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / 1048576.0:F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }

        private static void CheckSpriteRendererRef(Object renderer, Dictionary<Object, AssetItem> itemByObject, List<CheckIssue> issues)
        {
            var typeDict = SafeToType(renderer);
            if (typeDict == null || !HasField(typeDict, "m_Sprite"))
                return;

            if (TryResolveFieldPPtr(typeDict, renderer.assetsFile, "m_Sprite", out Object _))
                return;

            // Only report when the field exists and points somewhere (non-null path)
            if (!TryReadFieldPPtr(typeDict, "m_Sprite", out _, out var pathId) || pathId == 0)
                return;

            issues.Add(new CheckIssue
            {
                Severity = CheckSeverity.Error,
                Category = CheckCategory.MissingAtlasRef,
                Source = $"SpriteRenderer ({GetObjectName(renderer)})",
                Target = $"m_Sprite PathID {pathId}",
                Message = "SpriteRenderer.m_Sprite reference is missing.",
                NodePath = GetNodePath(renderer, itemByObject),
                TreeNode = GetTreeNode(renderer, itemByObject),
                Asset = renderer
            });
        }

        private static void CheckUiImageRef(MonoBehaviour mb, string className, Dictionary<Object, AssetItem> itemByObject,
            List<CheckIssue> issues)
        {
            var typeDict = SafeToType(mb);
            if (typeDict == null)
                return;

            var field = className == "RawImage" ? "m_Texture" : "m_Sprite";
            if (!HasField(typeDict, field))
                return;
            if (TryResolveFieldPPtr(typeDict, mb.assetsFile, field, out Object _))
                return;
            if (!TryReadFieldPPtr(typeDict, field, out _, out var pathId) || pathId == 0)
                return;

            issues.Add(new CheckIssue
            {
                Severity = CheckSeverity.Error,
                Category = CheckCategory.MissingAtlasRef,
                Source = $"{className} ({GetObjectName(mb)})",
                Target = $"{field} PathID {pathId}",
                Message = $"{className}.{field} reference is missing.",
                NodePath = GetNodePath(mb, itemByObject),
                TreeNode = GetTreeNode(mb, itemByObject),
                Asset = mb
            });
        }

        #region Helpers

        private static bool TryGetScriptClassName(MonoBehaviour mb, out string className)
        {
            className = null;
            if (mb?.m_Script == null || !mb.m_Script.TryGet(out MonoScript script))
                return false;
            className = script.m_ClassName;
            return !string.IsNullOrEmpty(className);
        }

        private static OrderedDictionary SafeToType(Object obj)
        {
            try
            {
                return obj.ToType();
            }
            catch
            {
                return null;
            }
        }

        private static string GetObjectName(Object obj)
        {
            switch (obj)
            {
                case NamedObject named when !string.IsNullOrEmpty(named.m_Name):
                    return named.m_Name;
                case MonoBehaviour mb when !string.IsNullOrEmpty(mb.m_Name):
                    return mb.m_Name;
                case GameObject go:
                    return go.m_Name;
                default:
                    return $"PathID {obj.m_PathID}";
            }
        }

        private static string GetNodePath(Object obj, Dictionary<Object, AssetItem> itemByObject)
        {
            return itemByObject.TryGetValue(obj, out var item) ? item.NodePath ?? string.Empty : string.Empty;
        }

        private static TreeNode GetTreeNode(Object obj, Dictionary<Object, AssetItem> itemByObject)
        {
            return itemByObject.TryGetValue(obj, out var item) ? item.TreeNode : null;
        }

        private static bool HasField(OrderedDictionary dict, string fieldName)
        {
            return dict != null && dict.Contains(fieldName);
        }

        private static bool TryReadPPtr(OrderedDictionary dict, out int fileId, out long pathId)
        {
            fileId = -1;
            pathId = 0;
            if (dict == null)
                return false;

            if (dict.Contains("m_FileID") && dict.Contains("m_PathID"))
            {
                fileId = Convert.ToInt32(dict["m_FileID"]);
                pathId = Convert.ToInt64(dict["m_PathID"]);
                return true;
            }
            if (dict.Contains("fileID") && dict.Contains("pathID"))
            {
                fileId = Convert.ToInt32(dict["fileID"]);
                pathId = Convert.ToInt64(dict["pathID"]);
                return true;
            }
            return false;
        }

        private static bool TryReadFieldPPtr(OrderedDictionary typeDict, string fieldName, out int fileId, out long pathId)
        {
            fileId = -1;
            pathId = 0;
            if (typeDict == null || !typeDict.Contains(fieldName))
                return false;
            return typeDict[fieldName] is OrderedDictionary pptrDict && TryReadPPtr(pptrDict, out fileId, out pathId);
        }

        private static bool TryResolveFieldPPtr(OrderedDictionary typeDict, SerializedFile assetsFile, string fieldName, out Object obj)
        {
            obj = null;
            if (!TryReadFieldPPtr(typeDict, fieldName, out var fileId, out var pathId))
                return false;
            if (pathId == 0)
                return true; // null reference counts as "resolved empty"
            if (fileId != 0)
                return false;
            return assetsFile.ObjectsDic.TryGetValue(pathId, out obj);
        }

        private static IEnumerable<(string label, bool resolved)> EnumerateArrayPPtrs(OrderedDictionary typeDict,
            SerializedFile assetsFile, string fieldName)
        {
            if (typeDict == null || !typeDict.Contains(fieldName) || !(typeDict[fieldName] is IList list))
                yield break;

            for (var i = 0; i < list.Count; i++)
            {
                var label = $"{fieldName}[{i}]";
                if (!(list[i] is OrderedDictionary pptrDict) || !TryReadPPtr(pptrDict, out var fileId, out var pathId))
                {
                    yield return (label, true);
                    continue;
                }
                if (pathId == 0)
                {
                    yield return (label, true);
                    continue;
                }
                if (fileId != 0)
                {
                    yield return ($"{label} PathID {pathId}", false);
                    continue;
                }
                var ok = assetsFile.ObjectsDic.ContainsKey(pathId);
                yield return ($"{label} PathID {pathId}", ok);
            }
        }

        private static IEnumerable<Object> ResolveArrayObjects(OrderedDictionary typeDict, SerializedFile assetsFile, string fieldName)
        {
            if (typeDict == null || !typeDict.Contains(fieldName) || !(typeDict[fieldName] is IList list))
                yield break;

            foreach (var entry in list)
            {
                if (!(entry is OrderedDictionary pptrDict) || !TryReadPPtr(pptrDict, out var fileId, out var pathId))
                    continue;
                if (fileId != 0 || pathId == 0)
                    continue;
                if (assetsFile.ObjectsDic.TryGetValue(pathId, out var obj))
                    yield return obj;
            }
        }

        private static TextAsset FindRelatedAtlasText(MonoBehaviour atlasMb)
        {
            // Prefer TypeTree field named atlasFile / atlasAsset
            var typeDict = SafeToType(atlasMb);
            if (typeDict != null)
            {
                foreach (var key in new[] { "atlasFile", "atlasAsset", "atlasText", "atlas" })
                {
                    if (TryResolveFieldPPtr(typeDict, atlasMb.assetsFile, key, out var obj) && obj is TextAsset ta)
                        return ta;
                }
            }

            // Fallback: same-name TextAsset ending with .atlas in the same file
            var baseName = GetObjectName(atlasMb);
            if (string.IsNullOrEmpty(baseName) || baseName.StartsWith("PathID", StringComparison.Ordinal))
                return null;

            foreach (var obj in atlasMb.assetsFile.Objects)
            {
                if (!(obj is TextAsset textAsset) || string.IsNullOrEmpty(textAsset.m_Name))
                    continue;
                var name = textAsset.m_Name;
                if (!(name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (name.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    baseName.IndexOf(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return textAsset;
                }
            }
            return null;
        }

        internal static List<string> ParseAtlasPages(string atlasText)
        {
            var pages = new List<string>();
            if (string.IsNullOrEmpty(atlasText))
                return pages;

            var lines = atlasText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line[0] == ' ' || line[0] == '\t' || line.IndexOf(':') >= 0)
                    continue;

                var sawPageProp = false;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j];
                    if (string.IsNullOrWhiteSpace(next))
                        break;
                    if (next[0] == ' ' || next[0] == '\t')
                        continue;
                    var colon = next.IndexOf(':');
                    if (colon <= 0)
                        break;
                    var key = next.Substring(0, colon).Trim();
                    if (key == "size" || key == "format" || key == "filter" || key == "repeat" || key == "pma")
                    {
                        sawPageProp = true;
                    }
                    break;
                }

                if (sawPageProp)
                    pages.Add(line.Trim());
            }
            return pages;
        }

        #endregion
    }
}
