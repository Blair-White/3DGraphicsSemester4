using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    public class ShapesApp : D3DApp
    {
        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;
        private DescriptorHeap _cbvHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>(1)
        {
            [RenderLayer.Opaque] = new List<RenderItem>()
        };

        private PassConstants _mainPassCB;

        private int _passCbvOffset;

        private bool _isWireframe = true;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = 0.2f * MathUtil.Pi;
        private float _radius = 15.0f;

        private Point _lastMousePos;

        public ShapesApp()
        {
            MainWindowCaption = "Shapes";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildShapeGeometry();
            BuildRenderItems();
            BuildFrameResources();
            BuildDescriptorHeaps();
            BuildConstantBufferViews();
            BuildPSOs();

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();
        }

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _proj = Matrix.PerspectiveFovLH(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            UpdateCamera();

            // Cycle through the circular frame resource array.
            _currFrameResourceIndex = (_currFrameResourceIndex + 1) % NumFrameResources;

            // Has the GPU finished processing the commands of the current frame resource?
            // If not, wait until the GPU has completed commands up to this fence point.
            if (CurrFrameResource.Fence != 0 && Fence.CompletedValue < CurrFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrFrameResource.Fence, CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }

            UpdateObjectCBs();
            UpdateMainPassCB(gt);
        }

        protected override void Draw(GameTimer gt)
        {
            CommandAllocator cmdListAlloc = CurrFrameResource.CmdListAlloc;

            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            cmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(cmdListAlloc, _isWireframe ? _psos["opaque_wireframe"] : _psos["opaque"]);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            int passCbvIndex = _passCbvOffset + _currFrameResourceIndex;
            GpuDescriptorHandle passCbvHandle = _cbvHeap.GPUDescriptorHandleForHeapStart;
            passCbvHandle += passCbvIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(1, passCbvHandle);

            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Present the buffer to the screen. Presenting will automatically swap the back and front buffers.
            SwapChain.Present(0, PresentFlags.None);

            // Advance the fence value to mark commands up to this fence point.
            CurrFrameResource.Fence = ++CurrentFence;

            // Add an instruction to the command queue to set a new fence point.
            // Because we are on the GPU timeline, the new fence point won't be
            // set until the GPU finishes processing all the commands prior to this Signal().
            CommandQueue.Signal(Fence, CurrentFence);
        }

        protected override void OnMouseDown(MouseButtons button, Point location)
        {
            base.OnMouseDown(button, location);
            _lastMousePos = location;
        }

        protected override void OnMouseMove(MouseButtons button, Point location)
        {
            if ((button & MouseButtons.Left) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.
                float dx = MathUtil.DegreesToRadians(0.25f * (location.X - _lastMousePos.X));
                float dy = MathUtil.DegreesToRadians(0.25f * (location.Y - _lastMousePos.Y));

                // Update angles based on input to orbit camera around box.
                _theta += dx;
                _phi += dy;

                // Restrict the angle mPhi.
                _phi = MathUtil.Clamp(_phi, 0.1f, MathUtil.Pi - 0.1f);
            }
            else if ((button & MouseButtons.Right) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.
                float dx = 0.05f * (location.X - _lastMousePos.X);
                float dy = 0.05f * (location.Y - _lastMousePos.Y);

                // Update the camera radius based on input.
                _radius += dx - dy;

                // Restrict the radius.
                _radius = MathUtil.Clamp(_radius, 5.0f, 150.0f);
            }

            _lastMousePos = location;
        }

        protected override void OnKeyDown(Keys keyCode)
        {
            if (keyCode == Keys.D1)
                _isWireframe = false;
        }

        protected override void OnKeyUp(Keys keyCode)
        {
            base.OnKeyUp(keyCode);
            if (keyCode == Keys.D1)
                _isWireframe = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rootSignature?.Dispose();
                _cbvHeap?.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
                foreach (MeshGeometry geometry in _geometries.Values) geometry.Dispose();
                foreach (PipelineState pso in _psos.Values) pso.Dispose();
            }
            base.Dispose(disposing);
        }

        private void UpdateCamera()
        {
            // Convert Spherical to Cartesian coordinates.
            _eyePos.X = _radius * MathHelper.Sinf(_phi) * MathHelper.Cosf(_theta);
            _eyePos.Z = _radius * MathHelper.Sinf(_phi) * MathHelper.Sinf(_theta);
            _eyePos.Y = _radius * MathHelper.Cosf(_phi);

            // Build the view matrix.
            _view = Matrix.LookAtLH(_eyePos, Vector3.Zero, Vector3.Up);
        }

        private void UpdateObjectCBs()
        {
            foreach (RenderItem e in _allRitems)
            {
                // Only update the cbuffer data if the constants have changed.
                // This needs to be tracked per frame resource.
                if (e.NumFramesDirty > 0)
                {
                    var objConstants = new ObjectConstants { World = Matrix.Transpose(e.World) };
                    CurrFrameResource.ObjectCB.CopyData(e.ObjCBIndex, ref objConstants);

                    // Next FrameResource need to be updated too.
                    e.NumFramesDirty--;
                }
            }
        }

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix viewProj = _view * _proj;
            Matrix invView = Matrix.Invert(_view);
            Matrix invProj = Matrix.Invert(_proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _mainPassCB.View = Matrix.Transpose(_view);
            _mainPassCB.InvView = Matrix.Transpose(invView);
            _mainPassCB.Proj = Matrix.Transpose(_proj);
            _mainPassCB.InvProj = Matrix.Transpose(invProj);
            _mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            _mainPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _mainPassCB.EyePosW = _eyePos;
            _mainPassCB.RenderTargetSize = new Vector2(ClientWidth, ClientHeight);
            _mainPassCB.InvRenderTargetSize = 1.0f / _mainPassCB.RenderTargetSize;
            _mainPassCB.NearZ = 1.0f;
            _mainPassCB.FarZ = 1000.0f;
            _mainPassCB.TotalTime = gt.TotalTime;
            _mainPassCB.DeltaTime = gt.DeltaTime;

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }

        private void BuildDescriptorHeaps()
        {
            int objCount = _allRitems.Count;

            // Need a CBV descriptor for each object for each frame resource,
            // +1 for the perPass CBV for each frame resource.
            int numDescriptors = (objCount + 1) * NumFrameResources;

            // Save an offset to the start of the pass CBVs.  These are the last 3 descriptors.
            _passCbvOffset = objCount * NumFrameResources;

            var cbvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = numDescriptors,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _cbvHeap = Device.CreateDescriptorHeap(cbvHeapDesc);
            _descriptorHeaps = new[] { _cbvHeap };
        }

        private void BuildConstantBufferViews()
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();

            int objCount = _allRitems.Count;

            // Need a CBV descriptor for each object for each frame resource.
            for (int frameIndex = 0; frameIndex < NumFrameResources; frameIndex++)
            {
                Resource objectCB = _frameResources[frameIndex].ObjectCB.Resource;
                for (int i = 0; i < objCount; i++)
                {
                    long cbAddress = objectCB.GPUVirtualAddress;

                    // Offset to the ith object constant buffer in the buffer.
                    cbAddress += i * objCBByteSize;

                    // Offset to the object cbv in the descriptor heap.
                    int heapIndex = frameIndex * objCount + i;
                    CpuDescriptorHandle handle = _cbvHeap.CPUDescriptorHandleForHeapStart;
                    handle += heapIndex * CbvSrvUavDescriptorSize;

                    var cbvDesc = new ConstantBufferViewDescription
                    {
                        BufferLocation = cbAddress,
                        SizeInBytes = objCBByteSize
                    };

                    Device.CreateConstantBufferView(cbvDesc, handle);
                }
            }

            int passCBByteSize = D3DUtil.CalcConstantBufferByteSize<PassConstants>();

            // Last three descriptors are the pass CBVs for each frame resource.
            for (int frameIndex = 0; frameIndex < NumFrameResources; frameIndex++)
            {
                Resource passCB = _frameResources[frameIndex].PassCB.Resource;
                long cbAddress = passCB.GPUVirtualAddress;

                // Offset to the pass cbv in the descriptor heap.
                int heapIndex = _passCbvOffset + frameIndex;
                CpuDescriptorHandle handle = _cbvHeap.CPUDescriptorHandleForHeapStart;
                handle += heapIndex * CbvSrvUavDescriptorSize;

                var cbvDesc = new ConstantBufferViewDescription
                {
                    BufferLocation = cbAddress,
                    SizeInBytes = passCBByteSize
                };

                Device.CreateConstantBufferView(cbvDesc, handle);
            }
        }

        private void BuildRootSignature()
        {
            var cbvTable0 = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 0);
            var cbvTable1 = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 1);

            // Root parameter can be a table, root descriptor or root constants.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.Vertex, cbvTable0),
                new RootParameter(ShaderVisibility.Vertex, cbvTable1)
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters);

            // Create a root signature with a single slot which points to a descriptor range consisting of a single constant buffer.
            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildShadersAndInputLayout()
        {
            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Color.hlsl", "VS", "vs_5_0");
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Color.hlsl", "PS", "ps_5_0");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
            });
        }

        private void BuildShapeGeometry()
        {
            //
            // We are concatenating all the geometry into one big vertex/index buffer. So
            // define the regions in the buffer each submesh covers.
            //

            var vertices = new List<Vertex>();
            var indices = new List<short>();

            //Primitives
            SubmeshGeometry box = AppendMeshData(GeometryGenerator.CreateBox(1.5f, 0.5f, 1.5f, 3), Color.DarkGreen, vertices, indices);
            SubmeshGeometry grid = AppendMeshData(GeometryGenerator.CreateGrid(50.0f, 15.0f , 2, 40), Color.ForestGreen, vertices,indices);
            SubmeshGeometry sphere = AppendMeshData(GeometryGenerator.CreateSphere(5.5f, 40, 40), Color.LightSeaGreen, vertices, indices);
            SubmeshGeometry cylinder = AppendMeshData(GeometryGenerator.CreateCylinder(0.5f, 0.3f, 3.0f, 20, 20), Color.SteelBlue, vertices, indices);
            SubmeshGeometry pyramid = AppendMeshData(GeometryGenerator.Pyramid(1.5f, 0.5f, 1.5f, 3), Color.Purple, vertices, indices);
            SubmeshGeometry wedge = AppendMeshData(GeometryGenerator.Wedge(1.5f, 0.5f, 1.5f, 3), Color.RosyBrown, vertices, indices);
            SubmeshGeometry diamond = AppendMeshData(GeometryGenerator.Diamond(1.5f, 0.5f, 1.5f, 3), Color.MintCream, vertices, indices);
            SubmeshGeometry triPrism = AppendMeshData(GeometryGenerator.TriangularPrism(1.5f, 0.5f, 1.5f, 3), Color.DarkOrchid, vertices, indices);
            SubmeshGeometry hexPrism = AppendMeshData(GeometryGenerator.HexagonalPrism(1.5f, 0.5f, 1.5f, 3), Color.RoyalBlue, vertices, indices);
            SubmeshGeometry cone = AppendMeshData(GeometryGenerator.CreateCone(0.5f, 0.3f, 3.0f, 20, 20), Color.SteelBlue, vertices, indices);
            //Special
            SubmeshGeometry water = AppendMeshData(GeometryGenerator.CreateBox(35.0f, 0.2f, 6.5f, 3), Color.LightBlue, vertices, indices);
            SubmeshGeometry water2 = AppendMeshData(GeometryGenerator.CreateBox(35.0f, 0.5f, 6.5f, 3), Color.Blue, vertices, indices);
            SubmeshGeometry pierBorder = AppendMeshData(GeometryGenerator.HexagonalPrism(0.5f, 0.1f, 2.5f, 3), Color.SlateGray, vertices, indices);
            SubmeshGeometry pierBox = AppendMeshData(GeometryGenerator.CreateBox(2.5f, 0.5f, 7.5f, 3), Color.LightSeaGreen, vertices, indices);
            //Main Concrete Floor
            SubmeshGeometry floor = AppendMeshData(GeometryGenerator.CreateBox(35, 0.5f, 20f, 36), Color.Black, vertices, indices);
            //Trees and Lights
            SubmeshGeometry treeBase = AppendMeshData(GeometryGenerator.CreateCylinder(0.15f, .05f, 1.0f, 20, 20), Color.SaddleBrown, vertices, indices);
            SubmeshGeometry treeTop = AppendMeshData(GeometryGenerator.CreateCone(0.5f, 0.3f, 1.5f, 20, 20), Color.ForestGreen, vertices, indices);
            SubmeshGeometry treeTop2 = AppendMeshData(GeometryGenerator.Diamond(0.5f, 0.3f, 0.5f, 3), Color.DarkOliveGreen, vertices, indices);
            SubmeshGeometry treeTop3 = AppendMeshData(GeometryGenerator.Pyramid(1.5f, 0.3f, 1.5f, 3), Color.DarkOrange, vertices, indices);
            SubmeshGeometry lightBase = AppendMeshData(GeometryGenerator.CreateCylinder(0.15f, .05f, 1.0f, 20, 20), Color.LightSlateGray, vertices, indices);
            SubmeshGeometry lightLight = AppendMeshData(GeometryGenerator.CreateSphere(0.2f, 20, 20), Color.LightGoldenrodYellow, vertices, indices);
            //boardwalk 
            SubmeshGeometry boardwalk = AppendMeshData(GeometryGenerator.CreateBox(35.0f, 0.5f, 2.5f, 24), Color.DarkGray, vertices, indices);
            //Street North
            SubmeshGeometry street0 = AppendMeshData(GeometryGenerator.CreateBox(3.0f, 0.5f, 18.0f, 24), Color.DarkGray, vertices, indices);
            //Square Buildings
            SubmeshGeometry buildingL = AppendMeshData(GeometryGenerator.CreateBox(1.5f, 9.5f, 1.5f, 3), Color.DimGray, vertices, indices);
            SubmeshGeometry buildingS = AppendMeshData(GeometryGenerator.CreateBox(3.5f, 7.5f, 3.5f, 3), Color.BlanchedAlmond, vertices, indices);
            //Pool of water - Park special
            SubmeshGeometry pool = AppendMeshData(GeometryGenerator.CreateCylinder(3.5f, -0.1f, 1.1f, 20, 20), Color.SteelBlue, vertices, indices);
            //GBC Tower
            SubmeshGeometry towerBase = AppendMeshData(GeometryGenerator.CreateCylinder(1.5f, 1.1f, 22.0f, 20, 20), Color.SteelBlue, vertices, indices);
            //Tower top boxes
            SubmeshGeometry towerBox = AppendMeshData(GeometryGenerator.CreateBox(4.5f, 0.4f, 4.5f, 3), Color.MediumSeaGreen, vertices, indices);
            //Tower Sphere
            SubmeshGeometry towerSphere = AppendMeshData(GeometryGenerator.CreateSphere(1.75f, 40, 40), Color.MediumPurple, vertices, indices);
            //Tower Top Cap
            SubmeshGeometry towerCap = AppendMeshData(GeometryGenerator.Diamond(2.5f, 2.5f, 2.5f, 3), Color.PaleVioletRed, vertices, indices);
            //Tower Borders
            SubmeshGeometry towerBorders = AppendMeshData(GeometryGenerator.TriangularPrism(0.2f, 0.5f, 1.55f, 3), Color.Gold, vertices, indices);
            //Dome Box
            SubmeshGeometry domeBox = AppendMeshData(GeometryGenerator.CreateBox(8.2f, 1.25f, 1.15f, 3), Color.IndianRed, vertices, indices);
            //Dome Wedge
            SubmeshGeometry domeWedge = AppendMeshData(GeometryGenerator.Wedge(0.2f, 0.25f, 0.05f, 3), Color.DeepPink, vertices, indices);

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "shapeGeo");

            geo.DrawArgs["box"] = box;
            geo.DrawArgs["grid"] = grid;
            geo.DrawArgs["sphere"] = sphere;
            geo.DrawArgs["cylinder"] = cylinder;
            geo.DrawArgs["pyramid"] = pyramid;
            geo.DrawArgs["wedge"] = wedge;
            geo.DrawArgs["diamond"] = diamond;
            geo.DrawArgs["triPrism"] = triPrism;
            geo.DrawArgs["hexPrism"] = hexPrism;
            geo.DrawArgs["cone"] = cone;
            geo.DrawArgs["water"] = water;
            geo.DrawArgs["water2"] = water2;
            geo.DrawArgs["pierBorder"] = pierBorder;
            geo.DrawArgs["pierBox"] = pierBox;
            geo.DrawArgs["floor"] = floor;
            geo.DrawArgs["treeBase"] = treeBase;
            geo.DrawArgs["treeTop"] = treeTop;
            geo.DrawArgs["treeTop2"] = treeTop2;
            geo.DrawArgs["treeTop3"] = treeTop3;
            geo.DrawArgs["lightBase"] = lightBase;
            geo.DrawArgs["lightLight"] = lightLight;
            geo.DrawArgs["boardwalk"] = boardwalk;
            geo.DrawArgs["buildingL"] = buildingL;
            geo.DrawArgs["buildingS"] = buildingS;
            geo.DrawArgs["street0"] = street0;
            geo.DrawArgs["pool"] = pool;
            geo.DrawArgs["towerBase"] = towerBase;
            geo.DrawArgs["towerBox"] = towerBox;
            geo.DrawArgs["towerSphere"] = towerSphere;
            geo.DrawArgs["towerCap"] = towerCap;
            geo.DrawArgs["towerBorders"] = towerBorders;
            geo.DrawArgs["domeBox"] = domeBox;
            geo.DrawArgs["domeWedge"] = domeWedge;
            _geometries[geo.Name] = geo;
        }

        private SubmeshGeometry AppendMeshData(GeometryGenerator.MeshData meshData, Color color, List<Vertex> vertices, List<short> indices)
        {
            //
            // Define the SubmeshGeometry that cover different
            // regions of the vertex/index buffers.
            //

            var submesh = new SubmeshGeometry
            {
                IndexCount = meshData.Indices32.Count,
                StartIndexLocation = indices.Count,
                BaseVertexLocation = vertices.Count
            };

            //
            // Extract the vertex elements we are interested in and pack the
            // vertices and indices of all the meshes into one vertex/index buffer.
            //

            vertices.AddRange(meshData.Vertices.Select(vertex => new Vertex
            {
                Pos = vertex.Position,
                Color = color.ToVector4()
            }));
            indices.AddRange(meshData.GetIndices16());

            return submesh;
        }

        private void BuildPSOs()
        {
            //
            // PSO for opaque objects.
            //

            var opaquePsoDesc = new GraphicsPipelineStateDescription
            {
                InputLayout = _inputLayout,
                RootSignature = _rootSignature,
                VertexShader = _shaders["standardVS"],
                PixelShader = _shaders["opaquePS"],
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = int.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePsoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _psos["opaque"] = Device.CreateGraphicsPipelineState(opaquePsoDesc);

            //
            // PSO for opaque wireframe objects.
            //

            var opaqueWireframePsoDesc = opaquePsoDesc;
            opaqueWireframePsoDesc.RasterizerState.FillMode = FillMode.Wireframe;

            _psos["opaque_wireframe"] = Device.CreateGraphicsPipelineState(opaqueWireframePsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 1, _allRitems.Count));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildRenderItems()
        {
            //Begin Creating objects
            int objCBIndex = 0;
            //Water            
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "water",
            world: Matrix.Translation(3.0f, -1.5f, -2.5f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "water2",
            world: Matrix.Translation(3.0f, -2.0f, -2.5f));
            //Pier
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "pierBorder",
            world: Matrix.Translation(-6.0f, -1.5f, -3));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "pierBorder",
            world: Matrix.Translation(-2.0f, -1.5f, -3));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "pierBox",
            world: Matrix.Translation(-4.0f, -1.5f, -3));
           //Main Flooring
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "floor",
            world: Matrix.Translation(3, -1.5f, 11));
            //boardwalk
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "boardwalk",
            world: Matrix.Translation(3, -1.4f, 2.5f));
            //streete
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "street0",
            world: Matrix.Translation(-3.5f, -1.4f, 12.5f));
            //pool
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "pool",
           world: Matrix.Translation(-11.0f, -1.2f, 16.5f));
            //Poolside trees
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeBase",
              world: Matrix.Translation(-11, -0.75f, 11.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeTop3",
              world: Matrix.Translation(-11, 0.25f, 11.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeBase",
              world: Matrix.Translation(-13, -0.75f, 12.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeTop2",
              world: Matrix.Translation(-13, 0.25f, 12.55f));
            
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeBase",
              world: Matrix.Translation(-7, -0.75f, 14.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeTop3",
              world: Matrix.Translation(-7, 0.25f, 14.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeBase",
              world: Matrix.Translation(-6, -0.75f, 16.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeTop2",
              world: Matrix.Translation(-6, 0.25f, 16.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeBase",
              world: Matrix.Translation(-9, -0.75f, 12.55f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeTop",
              world: Matrix.Translation(-9, 0.25f, 12.55f));
            //Lakeside Treeline/Lights
            for (int i = 0; i < 8; i++)
            {
               AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeBase",
               world: Matrix.Translation(-13+i*4.5f, -0.75f, 1.55f));
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "treeTop",
               world: Matrix.Translation(-13 + i * 4.5f, 0.25f, 1.55f));
            }
            for (int i = 0; i < 7; i++)
            {
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "lightBase",
                world: Matrix.Translation(-11 + i * 4.5f, -0.75f, 4.25f));
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "lightLight",
               world: Matrix.Translation(-11 + i * 4.5f, -0.1f, 4.25f));
            }

            //Buildings, Large
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "buildingL",
             world: Matrix.Translation(-12, 3, 6.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "buildingL",
             world: Matrix.Translation(-6, 3, 9.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "buildingL",
            world: Matrix.Translation(1, 3, 7.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "buildingL",
            world: Matrix.Translation(1, 3, 15.25f));
            //Buildings, Small
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "buildingS",
            world: Matrix.Translation(-8, 2.25f, 6.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "buildingS",
            world: Matrix.Translation(2, 2.25f, 12.25f));
            //Dome
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "sphere",
            world: Matrix.Translation(12, -1, 12.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "domeBox",
            world: Matrix.Translation(12, -1, 6.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "domeWedge",
            world: Matrix.Translation(9, 1, 6.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "domeWedge",
            world: Matrix.Translation(12, 1, 6.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "domeWedge",
            world: Matrix.Translation(15, 1, 6.25f));
            //Tower
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerBase",
            world: Matrix.Translation(8, 9.5f, 18.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerBox",
            world: Matrix.Translation(8, 21.5f, 18.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerBox",
            world: Matrix.Translation(8, 19.5f, 18.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerSphere",
            world: Matrix.Translation(8, 20.5f, 18.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerCap",
            world: Matrix.Translation(8, 25.5f, 18.25f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerBorders",
           world: Matrix.Translation(6, 22.25f, 18.00f));
            AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "towerBorders",
          world: Matrix.Translation(10, 22.25f, 18.00f));
        //Dome Ramps
            


            //KEEP BELOW FOR REFERENCE 
            //AddRenderItem(RenderLayer.Opaque, 0, "shapeGeo", "box",
            //    world: Matrix.Scaling(2.0f, 2.0f, 2.0f) * Matrix.Translation(0.0f, 0.5f, 0.0f));
            // AddRenderItem(RenderLayer.Opaque, 1, "shapeGeo", "grid");

            //Demo Starts Here
            //AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "cylinder",
            //    world: Matrix.Translation(-5.0f, 1.5f, -10.0f + i * 5.0f));
            //AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "cylinder",
            //    world: Matrix.Translation(+5.0f, 1.5f, -10.0f + i * 5.0f));

            //AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "sphere",
            //    world: Matrix.Translation(-5.0f, 3.5f, -10.0f + i * 5.0f));
            //AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "sphere",
            //    world: Matrix.Translation(+5.0f, 3.5f, -10.0f + i * 5.0f));
            //Demo End

            /// Testing start here
            /// 

            //Working shapes. test.
            //Pyramid Test
            //  AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "pyramid",
            //      world: Matrix.Translation(0.0f, 3.5f, -10.0f + i * 5.0f));
            ///Wedge Test
            //  AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "wedge",
            //      world: Matrix.Translation(3.0f, -1.5f, +0.0f + i * 5.0f));
            //Diamond Test
            // AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "diamond",
            //     world: Matrix.Translation(3.0f, -1.5f, +0.0f + i * 5.0f));
            // AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "triPrism",
            //    world: Matrix.Translation(3.0f, -1.5f, +0.0f + i * 5.0f));
            // AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "hexPrism",
            //    world: Matrix.Translation(3.0f, -1.5f, +0.0f + i * 5.0f));       
        }

        private void AddRenderItem(RenderLayer layer, int objCBIndex, string geoName, string submeshName, Matrix? world = null)
        {
            MeshGeometry geo = _geometries[geoName];
            SubmeshGeometry submesh = geo.DrawArgs[submeshName];
            var renderItem = new RenderItem
            {
                ObjCBIndex = objCBIndex,
                Geo = geo,
                IndexCount = submesh.IndexCount,
                StartIndexLocation = submesh.StartIndexLocation,
                BaseVertexLocation = submesh.BaseVertexLocation,
                World = world ?? Matrix.Identity
            };
            _ritemLayers[layer].Add(renderItem);
            _allRitems.Add(renderItem);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            // For each render item...
            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                // Offset to the CBV in the descriptor heap for this object and for this frame resource.
                int cbvIndex = _currFrameResourceIndex * _allRitems.Count + ri.ObjCBIndex;
                GpuDescriptorHandle cbvHandle = _cbvHeap.GPUDescriptorHandleForHeapStart;
                cbvHandle += cbvIndex * CbvSrvUavDescriptorSize;

                cmdList.SetGraphicsRootDescriptorTable(0, cbvHandle);

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }
    }
}
