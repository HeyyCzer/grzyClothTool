using CodeWalker.GameFiles;

namespace grzyClothTool.Optimization.Lods;

public enum LodIssueSeverity
{
    /// <summary>Probably only visual (wrong culling, odd skinning); the game still reads the file safely.</summary>
    Warning,
    /// <summary>The game or the GPU driver may read out of bounds or render garbage; the model must not be shipped.</summary>
    Error
}

/// <param name="Level">"High", "Medium", "Low" or "VeryLow".</param>
public sealed record LodIssue(LodIssueSeverity Severity, string Drawable, string Level, string Message)
{
    public override string ToString() => $"{Drawable} [{Level}] {Severity.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>
/// Structural checks of the models of a .ydd, meant to catch LODs that crash the game instead of only looking bad:
/// buffer sizes and index ranges (what the GPU driver reads), the vertex layout against the High model with the same
/// shader, the skinning (blend indices inside the bone table, bones the High model uses), empty or degenerate
/// geometries, NaN positions and geometry bounds. The High model is the reference for the other levels.
/// </summary>
public static class LodValidator
{
    private const int MaxVertices = ushort.MaxValue; // 16-bit index buffers and DrawableGeometry.VerticesCount
    private const int MaxListed = 5;

    /// <summary>Validates every LOD of every drawable of <paramref name="ydd"/>.</summary>
    /// <param name="generated">(drawable, level) pairs generated from the High model; see <see cref="ValidateModels"/>.</param>
    public static List<LodIssue> Validate(YddFile ydd, IReadOnlySet<(string Drawable, string Level)>? generated = null)
    {
        var issues = new List<LodIssue>();
        var drawables = ydd.DrawableDict?.Drawables?.data_items ?? [];
        for (int i = 0; i < drawables.Length; i++)
        {
            var drawable = drawables[i];
            var models = drawable?.DrawableModels;
            if (models == null)
            {
                continue;
            }

            var name = NameOf(drawable!, i);
            int shaderCount = drawable!.ShaderGroup?.Shaders?.data_items?.Length ?? 0;
            bool Generated(string level) => generated?.Contains((name, level)) == true;
            issues.AddRange(ValidateModels(models.High, null, shaderCount, name, "High"));
            issues.AddRange(ValidateModels(models.Med, models.High, shaderCount, name, "Medium", Generated("Medium")));
            issues.AddRange(ValidateModels(models.Low, models.High, shaderCount, name, "Low", Generated("Low")));
            issues.AddRange(ValidateModels(models.VLow, models.High, shaderCount, name, "VeryLow", Generated("VeryLow")));
        }
        return issues;
    }

    /// <summary>
    /// Validates the models of one LOD level. With <paramref name="high"/> the layout and the skinning are also compared
    /// to the High models.
    /// </summary>
    /// <param name="generated">
    /// The models were decimated from the High model, so they must match it: same skinning, same vertex layout per
    /// shader, no weights on bones the High model does not use, no blend indices past the bone table. A difference
    /// means a broken export or bone mapping, and is an error. Hand-made LODs of working clothes break all of these
    /// (index 255 with weight is common, some Medium models are not skinned), so for them it is only a warning.
    /// Buffer and index checks are errors either way.
    /// </param>
    public static List<LodIssue> ValidateModels(DrawableModel[]? models, DrawableModel[]? high, int shaderCount,
        string drawable, string level, bool generated = false)
    {
        var issues = new List<LodIssue>();
        if (models == null || models.Length == 0)
        {
            return issues;
        }

        void Add(LodIssueSeverity severity, string message) => issues.Add(new LodIssue(severity, drawable, level, message));

        var highGeometries = high?.SelectMany(m => m?.Geometries ?? []).Where(g => g != null).ToList() ?? [];
        var highBones = high == null ? null : ReferencedBones(high);
        bool highSkinned = high?.Any(m => m?.HasSkin == 1) == true;
        var mismatch = generated ? LodIssueSeverity.Error : LodIssueSeverity.Warning;

        for (int m = 0; m < models.Length; m++)
        {
            var model = models[m];
            var geometries = model?.Geometries;
            if (model == null || geometries == null || geometries.Length == 0)
            {
                Add(LodIssueSeverity.Error, $"model {m} has no geometries");
                continue;
            }

            if (highSkinned && model.HasSkin != 1)
            {
                Add(mismatch, $"model {m} is not skinned but the High model is");
            }

            for (int g = 0; g < geometries.Length; g++)
            {
                var geometry = geometries[g];
                var where = models.Length > 1 ? $"model {m} geometry {g}" : $"geometry {g}";
                if (geometry == null)
                {
                    Add(LodIssueSeverity.Error, $"{where} is null");
                    continue;
                }

                if (shaderCount > 0 && geometry.ShaderID >= shaderCount)
                {
                    Add(LodIssueSeverity.Error, $"{where} uses shader {geometry.ShaderID}, the drawable has {shaderCount}");
                }

                var bones = ValidateGeometry(geometry, model.HasSkin == 1, where, Add, mismatch);
                if (high == null || bones == null)
                {
                    continue;
                }

                // LODs may have shaders of their own (vanilla clothes do); only a shared shader has a reference layout.
                if (highGeometries.FirstOrDefault(h => h.ShaderID == geometry.ShaderID) is { } reference)
                {
                    CompareLayout(geometry, reference, where, Add, mismatch);
                }

                var foreign = bones.Where(b => !highBones!.Contains(b)).Order().ToList();
                if (foreign.Count > 0)
                {
                    Add(mismatch, $"{where} is skinned to bones the High model does not use: {List(foreign)}");
                }
            }
        }

        return issues;
    }

    /// <summary>Checks one geometry on its own. Returns the skeleton bones it is skinned to (null when unreadable).</summary>
    /// <param name="skinned">
    /// Rigid models (props bound to a single bone by SkeletonBinding) carry blend weights the game ignores, often all 0.
    /// </param>
    private static HashSet<ushort>? ValidateGeometry(DrawableGeometry geometry, bool skinned, string where, Action<LodIssueSeverity, string> add,
        LodIssueSeverity skinning)
    {
        var data = geometry.VertexData ?? geometry.VertexBuffer?.Data1 ?? geometry.VertexBuffer?.Data2;
        var info = data?.Info ?? geometry.VertexBuffer?.Info;
        if (data?.VertexBytes == null || info == null)
        {
            add(LodIssueSeverity.Error, $"{where} has no vertex data");
            return null;
        }

        int vertexCount = data.VertexCount;
        int stride = info.Stride;
        bool buffersOk = true;
        void Fail(string message)
        {
            add(LodIssueSeverity.Error, $"{where} {message}");
            buffersOk = false;
        }

        if (vertexCount == 0) Fail("has no vertices");
        if (vertexCount > MaxVertices) Fail($"has {vertexCount} vertices, more than the {MaxVertices} 16-bit indices can address");
        if (stride != LayoutStride(info)) Fail($"declares a {stride} byte vertex but its layout takes {LayoutStride(info)} bytes");
        if (geometry.VertexBuffer is { } buffer)
        {
            if (buffer.VertexStride != stride) Fail($"vertex buffer stride is {buffer.VertexStride}, the layout says {stride}");
            if (buffer.VertexCount != vertexCount) Fail($"vertex buffer count is {buffer.VertexCount}, its data has {vertexCount}");
        }
        if (geometry.VerticesCount != 0 && geometry.VerticesCount != vertexCount)
        {
            Fail($"says {geometry.VerticesCount} vertices, its data has {vertexCount}");
        }
        if (data.VertexBytes.Length != (long)vertexCount * stride)
        {
            Fail($"vertex data is {data.VertexBytes.Length} bytes, {vertexCount} x {stride} = {(long)vertexCount * stride} expected");
        }

        var indices = geometry.IndexBuffer?.Indices;
        if (indices == null || indices.Length == 0)
        {
            Fail("has no indices");
        }
        else
        {
            if (indices.Length % 3 != 0) Fail($"has {indices.Length} indices, not a multiple of 3");
            if (geometry.IndexBuffer!.IndicesCount != indices.Length)
            {
                Fail($"index buffer count is {geometry.IndexBuffer.IndicesCount}, it holds {indices.Length}");
            }

            int outOfRange = indices.Count(i => i >= vertexCount);
            if (outOfRange > 0)
            {
                Fail($"has {outOfRange} indices past the last vertex (max index {indices.Max()}, {vertexCount} vertices)");
            }

            int triangles = indices.Length / 3, degenerate = 0;
            for (int t = 0; t < triangles; t++)
            {
                int a = indices[t * 3], b = indices[t * 3 + 1], c = indices[t * 3 + 2];
                if (a == b || b == c || a == c) degenerate++;
            }
            if (triangles > 0 && degenerate == triangles)
            {
                Fail("only has degenerate triangles");
            }
            else if (degenerate > 0 && degenerate * 10 > triangles)
            {
                add(LodIssueSeverity.Warning, $"{where} has {degenerate} of {triangles} triangles degenerate");
            }
        }

        // Reading the vertices only makes sense when the buffer matches its declaration.
        if (!buffersOk)
        {
            return null;
        }

        CheckPositions(data, info, geometry.AABB, where, add);
        return skinned ? CheckSkinning(data, info, geometry.BoneIds, where, add, skinning) : [];
    }

    /// <summary>
    /// NaN positions and the geometry bounding box. The drawable bounds are not checked: ped components often have
    /// tiny or stale ones and the game does not cull them by it.
    /// </summary>
    private static void CheckPositions(VertexData data, VertexDeclaration info, AABB_s aabb, string where,
        Action<LodIssueSeverity, string> add)
    {
        int position = (int)VertexSemantics.Position;
        if (!HasComponent(info, position)
            || info.GetComponentType(position) is not (VertexComponentType.Float3 or VertexComponentType.Float4))
        {
            return;
        }

        int offset = info.GetComponentOffset(position);
        var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
        int invalid = 0;
        for (int v = 0; v < data.VertexCount; v++)
        {
            for (int k = 0; k < 3; k++)
            {
                float value = BitConverter.ToSingle(data.VertexBytes, v * info.Stride + offset + k * 4);
                if (!float.IsFinite(value))
                {
                    invalid++;
                    break;
                }
                min[k] = Math.Min(min[k], value);
                max[k] = Math.Max(max[k], value);
            }
        }

        if (invalid > 0)
        {
            add(LodIssueSeverity.Error, $"{where} has {invalid} vertices with NaN/infinite positions");
            return;
        }
        if (data.VertexCount == 0)
        {
            return;
        }

        float[] boxMin = [aabb.Min.X, aabb.Min.Y, aabb.Min.Z], boxMax = [aabb.Max.X, aabb.Max.Y, aabb.Max.Z];
        if (!Contains(boxMin, boxMax, min, max))
        {
            add(LodIssueSeverity.Warning, $"{where} vertices {Box(min, max)} go past its bounding box {Box(boxMin, boxMax)}");
        }
    }

    /// <summary>
    /// Blend indices point into the geometry's bone table (BoneIds), whose entries are skeleton bones; without a table
    /// they are skeleton bones themselves. Returns the skeleton bones carrying weight (empty when not skinned).
    /// Indices past the table without weight are not reported: working clothes are full of them.
    /// </summary>
    private static HashSet<ushort> CheckSkinning(VertexData data, VertexDeclaration info, ushort[]? boneIds, string where,
        Action<LodIssueSeverity, string> add, LodIssueSeverity skinning)
    {
        var bones = new HashSet<ushort>();
        int weightsSemantic = (int)VertexSemantics.BlendWeights, indicesSemantic = (int)VertexSemantics.BlendIndices;
        bool hasWeights = HasComponent(info, weightsSemantic), hasIndices = HasComponent(info, indicesSemantic);
        if (!hasWeights && !hasIndices)
        {
            return bones;
        }
        if (hasWeights != hasIndices)
        {
            add(LodIssueSeverity.Error, $"{where} has blend {(hasWeights ? "weights without indices" : "indices without weights")}");
            return bones;
        }
        if (Size(info, weightsSemantic) != 4 || Size(info, indicesSemantic) != 4)
        {
            add(LodIssueSeverity.Warning, $"{where} has blend weights/indices in an unexpected format; skinning not checked");
            return bones;
        }
        bool hasTable = boneIds is { Length: > 0 };

        int weightsOffset = info.GetComponentOffset(weightsSemantic), indicesOffset = info.GetComponentOffset(indicesSemantic);
        int outOfTable = 0, badSum = 0, noWeight = 0, maxIndex = 0;
        var bytes = data.VertexBytes;
        for (int v = 0; v < data.VertexCount; v++)
        {
            int baseOffset = v * info.Stride;
            int sum = 0;
            for (int k = 0; k < 4; k++)
            {
                int weight = bytes[baseOffset + weightsOffset + k];
                int index = bytes[baseOffset + indicesOffset + k];
                sum += weight;
                if (weight == 0)
                {
                    continue;
                }
                if (!hasTable)
                {
                    bones.Add((ushort)index);
                }
                else if (index < boneIds!.Length)
                {
                    bones.Add(boneIds[index]);
                }
                else
                {
                    outOfTable++;
                    maxIndex = Math.Max(maxIndex, index);
                }
            }

            if (sum == 0) noWeight++;
            else if (Math.Abs(sum - 255) > 3) badSum++;
        }

        if (outOfTable > 0)
        {
            add(skinning, $"{where} has {outOfTable} weighted blend indices past its bone table (index {maxIndex}, table of {boneIds!.Length})");
        }
        if (noWeight > 0)
        {
            add(LodIssueSeverity.Warning, $"{where} has {noWeight} vertices without any bone weight (they collapse to the origin)");
        }
        if (badSum > 0)
        {
            add(LodIssueSeverity.Warning, $"{where} has {badSum} vertices whose bone weights do not add up to 1");
        }

        return bones;
    }

    /// <summary>The LOD is drawn with the same shader as the High geometry: it must provide what that shader reads.</summary>
    private static void CompareLayout(DrawableGeometry geometry, DrawableGeometry reference, string where,
        Action<LodIssueSeverity, string> add, LodIssueSeverity mismatch)
    {
        var info = (geometry.VertexData ?? geometry.VertexBuffer?.Data1)?.Info ?? geometry.VertexBuffer?.Info;
        var expected = (reference.VertexData ?? reference.VertexBuffer?.Data1)?.Info ?? reference.VertexBuffer?.Info;
        if (info == null || expected == null || (info.Flags == expected.Flags && info.Types == expected.Types))
        {
            return;
        }

        var missing = new List<string>();
        var extra = new List<string>();
        var retyped = new List<string>();
        for (int k = 0; k < 16; k++)
        {
            bool has = HasComponent(info, k), wanted = HasComponent(expected, k);
            var name = ((VertexSemantics)k).ToString();
            if (wanted && !has) missing.Add(name);
            else if (has && !wanted) extra.Add(name);
            else if (has && info.GetComponentType(k) != expected.GetComponentType(k))
            {
                retyped.Add($"{name} {info.GetComponentType(k)} (High: {expected.GetComponentType(k)})");
            }
        }

        if (missing.Count > 0)
        {
            add(mismatch, $"{where} vertex layout lacks {string.Join(", ", missing)} that the High geometry with the same shader has");
        }
        if (retyped.Count > 0)
        {
            add(mismatch, $"{where} vertex layout differs from the High geometry: {string.Join(", ", retyped)}");
        }
        if (extra.Count > 0)
        {
            add(LodIssueSeverity.Warning, $"{where} vertex layout has {string.Join(", ", extra)} that the High geometry does not");
        }
    }

    /// <summary>Skeleton bones carrying weight in the High geometries; unreadable geometries are skipped.</summary>
    private static HashSet<ushort> ReferencedBones(DrawableModel[] models)
    {
        var bones = new HashSet<ushort>();
        foreach (var geometry in models.Where(m => m?.HasSkin == 1).SelectMany(m => m.Geometries ?? []).Where(g => g != null))
        {
            var data = geometry.VertexData ?? geometry.VertexBuffer?.Data1;
            if (data?.VertexBytes == null || data.Info == null || data.VertexBytes.Length != (long)data.VertexCount * data.Info.Stride)
            {
                continue;
            }
            bones.UnionWith(CheckSkinning(data, data.Info, geometry.BoneIds, "", (_, _) => { }, LodIssueSeverity.Warning));
        }
        return bones;
    }

    private static bool HasComponent(VertexDeclaration info, int semantic) => ((info.Flags >> semantic) & 1) == 1;

    private static int Size(VertexDeclaration info, int semantic) => VertexComponentTypes.GetSizeInBytes(info.GetComponentType(semantic));

    private static int LayoutStride(VertexDeclaration info)
    {
        int stride = 0;
        for (int k = 0; k < 16; k++)
        {
            if (HasComponent(info, k))
            {
                stride += Size(info, k);
            }
        }
        return stride;
    }

    /// <summary>Box containment with a small tolerance: exporters round the bounds.</summary>
    private static bool Contains(float[] boxMin, float[] boxMax, float[] min, float[] max)
    {
        for (int k = 0; k < 3; k++)
        {
            float tolerance = 0.01f + 0.01f * Math.Abs(boxMax[k] - boxMin[k]);
            if (min[k] < boxMin[k] - tolerance || max[k] > boxMax[k] + tolerance)
            {
                return false;
            }
        }
        return true;
    }

    private static string Box(float[] min, float[] max) =>
        $"({min[0]:0.###}, {min[1]:0.###}, {min[2]:0.###})-({max[0]:0.###}, {max[1]:0.###}, {max[2]:0.###})";

    private static string List(List<ushort> values) =>
        string.Join(", ", values.Take(MaxListed)) + (values.Count > MaxListed ? $" (+{values.Count - MaxListed})" : "");

    private static string NameOf(Drawable drawable, int index)
    {
        var name = drawable.Name;
        if (string.IsNullOrEmpty(name))
        {
            return $"drawable {index}";
        }
        return name.EndsWith("#dd", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }
}
