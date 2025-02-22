using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[ExecuteInEditMode]
public class GaussianSplatRenderer : MonoBehaviour
{
    public enum RenderMode
    {
        Splats,
        DebugPoints,
        DebugPointIndices,
        DebugBoxes,
        DebugChunkBounds,
    }

    public enum DisplayDataMode
    {
        None = 0,
        Position = 1,
        Scale = 2,
        Rotation = 3,
        Color = 4,
        Opacity = 5,
        SH1, SH2, SH3, SH4, SH5, SH6, SH7, SH8, SH9, SH10, SH11, SH12, SH13, SH14, SH15,
    }

    [Header("Data Asset")]

    public GaussianSplatAsset m_Asset;

    [Header("Render Options")]

    [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
    public float m_SplatScale = 1.0f;
    [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
    public int m_SHOrder = 3;
    [Range(1,30)] [Tooltip("Sort splats only every N frames")]
    public int m_SortNthFrame = 1;

    [Header("Debugging Tweaks")]

    public RenderMode m_RenderMode = RenderMode.Splats;
    [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;
    public DisplayDataMode m_DisplayData = DisplayDataMode.None;
    [Range(1, 8)] public int m_DisplayDataScale = 2;
    public bool m_RenderInSceneView = true;
    [Tooltip("Use AMD FidelityFX sorting when available, instead of the slower bitonic sort")]
    public bool m_PreferFfxSort = true; // use AMD FidelityFX sort if available (currently: DX12, Vulkan, Metal, but *not* DX11)

    [Header("Resources")]

    public Shader m_ShaderSplats;
    public Shader m_ShaderComposite;
    public Shader m_ShaderDebugPoints;
    public Shader m_ShaderDebugBoxes;
    public Shader m_ShaderDebugData;
    [Tooltip("Gaussian splatting utilities compute shader")]
    public ComputeShader m_CSSplatUtilities;
    [Tooltip("'Island' bitonic sort compute shader")]
    [FormerlySerializedAs("m_CSGpuSort")]
    public ComputeShader m_CSIslandSort;
    [Tooltip("AMD FidelityFX sort compute shader")]
    public ComputeShader m_CSFfxSort;

    GraphicsBuffer m_GpuSortDistances;
    GraphicsBuffer m_GpuSortKeys;
    GraphicsBuffer m_GpuChunks;
    GraphicsBuffer m_GpuView;
    GraphicsBuffer m_GpuIndexBuffer;

    IslandGPUSort m_SorterIsland;
    IslandGPUSort.Args m_SorterIslandArgs;
    FfxParallelSort m_SorterFfx;
    FfxParallelSort.Args m_SorterFfxArgs;

    CommandBuffer m_RenderCommandBuffer;
    readonly HashSet<Camera> m_CameraCommandBuffersDone = new();

    Material m_MatSplats;
    Material m_MatComposite;
    Material m_MatDebugPoints;
    Material m_MatDebugBoxes;
    Material m_MatDebugData;

    int m_FrameCounter;
    GaussianSplatAsset m_PrevAsset;
    Hash128 m_PrevHash;

    static ProfilerMarker s_ProfSort = new ProfilerMarker(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfView = new ProfilerMarker(ProfilerCategory.Render, "GaussianSplat.View", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfDraw = new ProfilerMarker(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfCompose = new ProfilerMarker(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);

    public GaussianSplatAsset asset => m_Asset;

    public bool HasValidAsset => m_Asset != null && m_Asset.m_SplatCount > 0;
    public bool HasValidRenderSetup => m_RenderCommandBuffer != null && m_GpuChunks != null;

    void CreateResourcesForAsset()
    {
        if (!HasValidAsset)
            return;
        m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, asset.m_Chunks.Length, UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) { name = "GaussianChunkData" };
        m_GpuChunks.SetData(asset.m_Chunks);

        m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.m_SplatCount, 40);
        m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
        // cube indices, most often we use only the first quad
        m_GpuIndexBuffer.SetData(new ushort[]
        {
            0, 1, 2, 1, 3, 2,
            4, 6, 5, 5, 6, 7,
            0, 2, 4, 4, 2, 6,
            1, 5, 3, 5, 7, 3,
            0, 4, 1, 4, 5, 1,
            2, 3, 6, 3, 7, 6
        });

        int splatCountNextPot = Mathf.NextPowerOfTwo(m_Asset.m_SplatCount);
        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4) { name = "GaussianSplatSortDistances" };
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4) { name = "GaussianSplatSortIndices" };

        // init keys buffer to splat indices
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetInt("_SplatCountPOT", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(0, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(0, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        m_SorterIslandArgs.keys = m_GpuSortDistances;
        m_SorterIslandArgs.values = m_GpuSortKeys;
        m_SorterIslandArgs.count = (uint)splatCountNextPot;

        m_SorterFfxArgs.inputKeys = m_GpuSortDistances;
        m_SorterFfxArgs.inputValues = m_GpuSortKeys;
        m_SorterFfxArgs.count = (uint) m_Asset.m_SplatCount;
        if (m_SorterFfx.Valid)
            m_SorterFfxArgs.resources = FfxParallelSort.SupportResources.Load((uint)m_Asset.m_SplatCount);
    }

    public void OnEnable()
    {
        Camera.onPreCull += OnPreCullCamera;

        m_FrameCounter = 0;
        m_RenderCommandBuffer = null;
        if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_ShaderDebugData == null || m_CSSplatUtilities == null || m_CSIslandSort == null)
            return;
        if (!SystemInfo.supportsComputeShaders)
            return;

        m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
        m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
        m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
        m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};
        m_MatDebugData = new Material(m_ShaderDebugData) {name = "GaussianDebugData"};
        
        m_SorterIsland = new IslandGPUSort(m_CSIslandSort);
        m_SorterFfx = new FfxParallelSort(m_CSFfxSort);
        m_RenderCommandBuffer = new CommandBuffer {name = "GaussianRender"};        
        
        CreateResourcesForAsset();
    }

    void OnPreCullCamera(Camera cam)
    {
        m_RenderCommandBuffer?.Clear();

        if (!HasValidRenderSetup)
            return;

        Material displayMat = m_RenderMode switch
        {
            RenderMode.DebugPoints => m_MatDebugPoints,
            RenderMode.DebugPointIndices => m_MatDebugPoints,
            RenderMode.DebugBoxes => m_MatDebugBoxes,
            RenderMode.DebugChunkBounds => m_MatDebugBoxes,
            _ => m_MatSplats
        };
        if (displayMat == null)
            return;

        if (!m_CameraCommandBuffersDone.Contains(cam))
        {
            cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_RenderCommandBuffer);
            m_CameraCommandBuffersDone.Add(cam);
        }

        SetAssetTexturesOnMaterial(displayMat);

        displayMat.SetBuffer("_SplatChunks", m_GpuChunks);
        displayMat.SetInteger("_SplatChunkCount", m_GpuChunks.count);

        displayMat.SetBuffer("_SplatViewData", m_GpuView);
        
        displayMat.SetBuffer("_OrderBuffer", m_GpuSortKeys);
        displayMat.SetFloat("_SplatScale", m_SplatScale);
        displayMat.SetFloat("_SplatSize", m_PointDisplaySize);
        displayMat.SetInteger("_SplatCount", m_Asset.m_SplatCount);
        displayMat.SetInteger("_SHOrder", m_SHOrder);
        displayMat.SetInteger("_DisplayIndex", m_RenderMode == RenderMode.DebugPointIndices ? 1 : 0);
        displayMat.SetInteger("_DisplayChunks", m_RenderMode == RenderMode.DebugChunkBounds ? 1 : 0);

        var matrix = transform.localToWorldMatrix;
        if (m_FrameCounter % m_SortNthFrame == 0)
            SortPoints(cam, matrix);
        ++m_FrameCounter;

        CalcViewData(cam, matrix);

        int indexCount = 6;
        int instanceCount = m_Asset.m_SplatCount;
        MeshTopology topology = MeshTopology.Triangles;
        if (m_RenderMode is RenderMode.DebugBoxes or RenderMode.DebugChunkBounds)
            indexCount = 36;
        if (m_RenderMode == RenderMode.DebugChunkBounds)
            instanceCount = m_GpuChunks.count;

        int rtNameID = Shader.PropertyToID("_GaussianSplatRT");
        if (cam.cameraType != CameraType.Preview && (m_RenderInSceneView || cam.cameraType != CameraType.SceneView))
        {
            m_RenderCommandBuffer.GetTemporaryRT(rtNameID, -1, -1, 0, FilterMode.Point,
                GraphicsFormat.R16G16B16A16_SFloat);
            m_RenderCommandBuffer.BeginSample(s_ProfDraw);
            m_RenderCommandBuffer.SetRenderTarget(rtNameID, BuiltinRenderTextureType.CurrentActive);
            m_RenderCommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);
            m_RenderCommandBuffer.DrawProcedural(m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount);
            m_RenderCommandBuffer.EndSample(s_ProfDraw);
            m_RenderCommandBuffer.BeginSample(s_ProfCompose);
            m_RenderCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_RenderCommandBuffer.DrawProcedural(Matrix4x4.identity, m_MatComposite, 0, MeshTopology.Triangles, 6, 1);
            m_RenderCommandBuffer.EndSample(s_ProfCompose);
            m_RenderCommandBuffer.ReleaseTemporaryRT(rtNameID);
        }

        if (m_DisplayData != DisplayDataMode.None)
        {
            SetAssetTexturesOnMaterial(m_MatDebugData);
            m_MatDebugData.SetBuffer("_SplatChunks", m_GpuChunks);
            m_MatDebugData.SetInteger("_SplatCount", m_Asset.m_SplatCount);
            m_MatDebugData.SetInteger("_DisplayMode", (int)m_DisplayData);
            m_MatDebugData.SetInteger("_DisplayDataScale", m_DisplayDataScale);
            Graphics.DrawProcedural(m_MatDebugData, new Bounds(cam.transform.position, Vector3.one * 1000.0f), MeshTopology.Triangles, 6, 1, cam);
        }
    }

    static string TextureTypeToPropertyName(GaussianSplatAsset.TexType type)
    {
        return type switch
        {
            GaussianSplatAsset.TexType.Pos => "_TexPos",
            GaussianSplatAsset.TexType.Rot => "_TexRot",
            GaussianSplatAsset.TexType.Scl => "_TexScl",
            GaussianSplatAsset.TexType.Col => "_TexCol",
            GaussianSplatAsset.TexType.SH1 => "_TexSH1",
            GaussianSplatAsset.TexType.SH2 => "_TexSH2",
            GaussianSplatAsset.TexType.SH3 => "_TexSH3",
            GaussianSplatAsset.TexType.SH4 => "_TexSH4",
            GaussianSplatAsset.TexType.SH5 => "_TexSH5",
            GaussianSplatAsset.TexType.SH6 => "_TexSH6",
            GaussianSplatAsset.TexType.SH7 => "_TexSH7",
            GaussianSplatAsset.TexType.SH8 => "_TexSH8",
            GaussianSplatAsset.TexType.SH9 => "_TexSH9",
            GaussianSplatAsset.TexType.SHA => "_TexSHA",
            GaussianSplatAsset.TexType.SHB => "_TexSHB",
            GaussianSplatAsset.TexType.SHC => "_TexSHC",
            GaussianSplatAsset.TexType.SHD => "_TexSHD",
            GaussianSplatAsset.TexType.SHE => "_TexSHE",
            GaussianSplatAsset.TexType.SHF => "_TexSHF",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    void SetAssetTexturesOnCS(ComputeShader cs, int kernelIndex)
    {
        uint texFlags = 0;
        for (var t = GaussianSplatAsset.TexType.Pos; t < GaussianSplatAsset.TexType.TypeCount; ++t)
        {
            var tex = m_Asset.GetTex(t);
            if (tex.graphicsFormat == GraphicsFormat.R32_SFloat) // so that a shader knows it needs to interpret R32F as packed integer
                texFlags |= (1u << (int) t);
            cs.SetTexture(kernelIndex, TextureTypeToPropertyName(t), tex);
        }
        cs.SetInt("_TexFlagBits", (int)texFlags);
    }

    void SetAssetTexturesOnMaterial(Material displayMat)
    {
        uint texFlags = 0;
        for (var t = GaussianSplatAsset.TexType.Pos; t < GaussianSplatAsset.TexType.TypeCount; ++t)
        {
            var tex = m_Asset.GetTex(t);
            if (tex.graphicsFormat == GraphicsFormat.R32_SFloat) // so that a shader knows it needs to interpret R32F as packed integer
                texFlags |= (1u << (int) t);
            displayMat.SetTexture(TextureTypeToPropertyName(t), tex);
        }

        displayMat.SetInteger("_TexFlagBits", (int)texFlags);
    }

    void DisposeResourcesForAsset()
    {
        if (m_CameraCommandBuffersDone != null)
        {
            if (m_RenderCommandBuffer != null)
            {
                foreach (var cam in m_CameraCommandBuffersDone)
                {
                    if (cam)
                        cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_RenderCommandBuffer);
                }
            }
            m_CameraCommandBuffersDone.Clear();
        }

        m_GpuChunks?.Dispose();
        m_GpuView?.Dispose();
        m_GpuIndexBuffer?.Dispose();
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_SorterFfxArgs.resources.Dispose();
        
        m_GpuChunks = null;
        m_GpuView = null;
        m_GpuIndexBuffer = null;
        m_GpuSortDistances = null;
        m_GpuSortKeys = null;
    }

    public void OnDisable()
    {
        DisposeResourcesForAsset();
        
        Camera.onPreCull -= OnPreCullCamera;

        m_RenderCommandBuffer?.Clear();
        m_RenderCommandBuffer = null;

        DestroyImmediate(m_MatSplats);
        DestroyImmediate(m_MatComposite);
        DestroyImmediate(m_MatDebugPoints);
        DestroyImmediate(m_MatDebugBoxes);
        DestroyImmediate(m_MatDebugData);
    }

    void CalcViewData(Camera cam, Matrix4x4 matrix)
    {
        if (cam.cameraType == CameraType.Preview || !m_RenderInSceneView && cam.cameraType == CameraType.SceneView)
            return;

        using var prof = s_ProfView.Auto();

        var tr = transform;

        Matrix4x4 matView = cam.worldToCameraMatrix;
        Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        Matrix4x4 matO2W = tr.localToWorldMatrix;
        Matrix4x4 matW2O = tr.worldToLocalMatrix;
        int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
        Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
        Vector4 camPos = cam.transform.position;
        
        // calculate view dependent data for each splat
        const int kernelIdx = 2;
        SetAssetTexturesOnCS(m_CSSplatUtilities, kernelIdx);
        
        m_CSSplatUtilities.SetInt("_SplatCount", m_GpuView.count);
        m_CSSplatUtilities.SetBuffer(kernelIdx, "_SplatViewData", m_GpuView);
        m_CSSplatUtilities.SetBuffer(kernelIdx, "_OrderBuffer", m_GpuSortKeys);
        m_CSSplatUtilities.SetBuffer(kernelIdx, "_SplatChunks", m_GpuChunks);
        
        m_CSSplatUtilities.SetMatrix("_MatrixVP", matProj * matView);
        m_CSSplatUtilities.SetMatrix("_MatrixV", matView);
        m_CSSplatUtilities.SetMatrix("_MatrixP", matProj);
        m_CSSplatUtilities.SetMatrix("_MatrixObjectToWorld", matO2W);
        m_CSSplatUtilities.SetMatrix("_MatrixWorldToObject", matW2O);
        
        m_CSSplatUtilities.SetVector("_VecScreenParams", screenPar);
        m_CSSplatUtilities.SetVector("_VecWorldSpaceCameraPos", camPos);
        m_CSSplatUtilities.SetFloat("_SplatScale", m_SplatScale);
        m_CSSplatUtilities.SetInt("_SHOrder", m_SHOrder);

        m_CSSplatUtilities.GetKernelThreadGroupSizes(kernelIdx, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(kernelIdx, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
    }

    void SortPoints(Camera cam, Matrix4x4 matrix)
    {
        if (cam.cameraType == CameraType.Preview || !m_RenderInSceneView && cam.cameraType == CameraType.SceneView)
            return;

        bool useFfx = m_PreferFfxSort && m_SorterFfx.Valid;
        Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
        if (useFfx)
        {
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;
        }

        // calculate distance to the camera for each splat
        int kernelIdx = 1;
        m_RenderCommandBuffer.BeginSample(s_ProfSort);
        var texPos = m_Asset.GetTex(GaussianSplatAsset.TexType.Pos);
        m_RenderCommandBuffer.SetComputeTextureParam(m_CSSplatUtilities, kernelIdx, "_TexPos", texPos);
        m_RenderCommandBuffer.SetComputeIntParam(m_CSSplatUtilities, "_TexFlagBits", texPos.graphicsFormat == GraphicsFormat.R32_SFloat ? 1 : 0);
        m_RenderCommandBuffer.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatSortDistances", m_GpuSortDistances);
        m_RenderCommandBuffer.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatSortKeys", m_GpuSortKeys);
        m_RenderCommandBuffer.SetComputeBufferParam(m_CSSplatUtilities, kernelIdx, "_SplatChunks", m_GpuChunks);
        m_RenderCommandBuffer.SetComputeMatrixParam(m_CSSplatUtilities, "_LocalToWorldMatrix", matrix);
        m_RenderCommandBuffer.SetComputeMatrixParam(m_CSSplatUtilities, "_WorldToCameraMatrix", worldToCamMatrix);
        m_RenderCommandBuffer.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_Asset.m_SplatCount);
        m_RenderCommandBuffer.SetComputeIntParam(m_CSSplatUtilities, "_SplatCountPOT", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kernelIdx, out uint gsX, out _, out _);
        m_RenderCommandBuffer.DispatchCompute(m_CSSplatUtilities, kernelIdx, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        // sort the splats
        if (useFfx)
            m_SorterFfx.Dispatch(m_RenderCommandBuffer, m_SorterFfxArgs);
        else
            m_SorterIsland.Dispatch(m_RenderCommandBuffer, m_SorterIslandArgs);
        m_RenderCommandBuffer.EndSample(s_ProfSort);
    }

    public void Update()
    {
        var curHash = m_Asset ? m_Asset.m_DataHash : new Hash128();
        if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
        {
            m_PrevAsset = m_Asset;
            m_PrevHash = curHash;
            DisposeResourcesForAsset();
            CreateResourcesForAsset();
        }
    }

    public void ActivateCamera(int index)
    {
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            var cam = m_Asset.m_Cameras[index];
            mainCam.transform.localPosition = cam.pos;
            mainCam.transform.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            #if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(mainCam.transform);
            #endif
        }
    }
}
