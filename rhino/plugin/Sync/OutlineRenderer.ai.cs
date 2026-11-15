using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RhinoClient.Sync;

/// <summary>
/// GLSL outlines beneath preview meshes, drawn with raw OpenGL inside a display conduit callback while Rhino's
/// context is current (Windows, Rhino 8 OpenGL pipeline). RhinoCommon has no custom-shader API.
///
/// Per mesh, before the shaded mesh itself:
///   1. mask:  rasterize the mesh into the stencil buffer (no colour, no depth writes);
///   2. hull:  draw it again with the vertex shader pushing each vertex outward along its welded normal by N screen
///             pixels; the fragment shader writes the outline colour only where the stencil is not set, so only a
///             band around the silhouette is coloured. Depth tested, so geometry in front still hides it;
///   3. clear: rasterize again to reset the stencil.
/// Every GL state it touches is saved and restored. Any failure (no stencil, GL below 3.3, not Windows) turns it
/// off for the session and <see cref="Draw"/> returns false, so the caller falls back to silhouette curves.
/// </summary>
internal static class OutlineRenderer
{
    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNrm;
        uniform mat4 uMVP;
        uniform vec2 uViewport;
        uniform float uWidth;
        void main() {
            vec4 c = uMVP * vec4(aPos, 1.0);
            if (uWidth > 0.0) {
                // screen-space direction of the normal: derivative of the perspective divide
                vec4 n = uMVP * vec4(aNrm, 0.0);
                vec2 d = (n.xy * c.w - c.xy * n.w) * uViewport;
                float l = length(d);
                if (l > 1e-9) c.xy += (d / l) * (2.0 * uWidth / uViewport) * c.w;
            }
            gl_Position = c;
        }
        """;

    private const string Fragment = """
        #version 330 core
        uniform vec4 uColor;
        out vec4 fragColor;
        void main() { fragColor = uColor; }
        """;

    private static bool _failed;
    private static Gl? _gl;
    private static readonly Dictionary<IntPtr, ContextState> Contexts = new();
    private static readonly ConditionalWeakTable<Mesh, Arrays> CpuArrays = new();

    internal static bool Available => !_failed && OperatingSystem.IsWindows();
    internal static string? Error { get; private set; }

    /// <summary>Draws outlines for the given meshes. Call before the meshes are shaded. False: use the fallback.</summary>
    internal static bool Draw(RhinoViewport viewport, IReadOnlyList<(Mesh Mesh, Color Color, float WidthPixels)> items)
    {
        if (!Available) return false;
        if (items.Count == 0) return true;
        try
        {
            var gl = _gl ??= new Gl();
            var context = gl.wglGetCurrentContext();
            if (context == IntPtr.Zero) throw new InvalidOperationException("No current OpenGL context.");
            if (!Contexts.TryGetValue(context, out var state)) Contexts[context] = state = new ContextState(gl);
            if (state.Buffers.Count > 4000) state.Release(gl);

            var saved = Saved.Read(gl);
            try
            {
                var worldToClip = viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip);
                var matrix = new float[16];
                for (var r = 0; r < 4; r++)
                    for (var c = 0; c < 4; c++)
                        matrix[r * 4 + c] = (float)worldToClip[r, c];
                var view = new int[4];
                gl.glGetIntegerv(Gl.Viewport, view);

                gl.UseProgram(state.Program);
                gl.UniformMatrix4fv(state.Mvp, 1, 1, matrix);
                gl.Uniform2f(state.ViewportSize, view[2], view[3]);
                gl.glEnable(Gl.StencilTest);
                gl.glEnable(Gl.DepthTest);
                gl.glDisable(Gl.CullFace);
                gl.glStencilMask(0xFF);
                gl.glDepthMask(0);
                foreach (var (mesh, color, width) in items)
                {
                    var buffers = state.BuffersFor(gl, mesh);
                    gl.BindVertexArray(buffers.Vao);
                    // 1. mask
                    gl.glColorMask(0, 0, 0, 0);
                    gl.glDepthFunc(Gl.Always);
                    gl.glStencilFunc(Gl.Always, 1, 0xFF);
                    gl.glStencilOp(Gl.Keep, Gl.Keep, Gl.Replace);
                    gl.Uniform1f(state.Width, 0f);
                    gl.glDrawElements(Gl.Triangles, buffers.Count, Gl.UnsignedInt, IntPtr.Zero);
                    // 2. hull outside the mask, depth tested against the scene
                    gl.glColorMask(1, 1, 1, 1);
                    gl.glDepthFunc(Gl.LessEqual);
                    gl.glStencilFunc(Gl.NotEqual, 1, 0xFF);
                    gl.glStencilOp(Gl.Keep, Gl.Keep, Gl.Keep);
                    gl.Uniform1f(state.Width, width);
                    gl.Uniform4f(state.Color, color.R / 255f, color.G / 255f, color.B / 255f, 1f);
                    gl.glDrawElements(Gl.Triangles, buffers.Count, Gl.UnsignedInt, IntPtr.Zero);
                    // 3. clear the mask
                    gl.glColorMask(0, 0, 0, 0);
                    gl.glDepthFunc(Gl.Always);
                    gl.glStencilFunc(Gl.Always, 0, 0xFF);
                    gl.glStencilOp(Gl.Keep, Gl.Keep, Gl.Replace);
                    gl.Uniform1f(state.Width, 0f);
                    gl.glDrawElements(Gl.Triangles, buffers.Count, Gl.UnsignedInt, IntPtr.Zero);
                }
            }
            finally
            {
                saved.Restore(gl);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Never break Rhino's frame: switch off and let the caller fall back.
            _failed = true;
            Error = ex.Message;
            Rhino.RhinoApp.WriteLine("RhinoClient: GLSL outlines are not available here, using silhouette curves (" + ex.Message + ").");
            return false;
        }
    }

    /// <summary>Interleaved positions and welded normals, so the pushed-out hull has no cracks at hard edges.</summary>
    private sealed class Arrays
    {
        internal float[] Vertices = Array.Empty<float>();
        internal uint[] Indices = Array.Empty<uint>();

        internal static Arrays From(Mesh mesh)
        {
            mesh.Normals.ComputeNormals();
            var count = mesh.Vertices.Count;
            var welded = new Vector3f[count];
            var groups = new Dictionary<(double, double, double), List<int>>();
            for (var k = 0; k < count; k++)
            {
                var p = mesh.Vertices[k];
                var key = (Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
                list.Add(k);
            }

            foreach (var list in groups.Values)
            {
                var sum = new Vector3f(0, 0, 0);
                foreach (var k in list) sum += mesh.Normals[k];
                var length = Math.Sqrt(sum.X * sum.X + sum.Y * sum.Y + sum.Z * sum.Z);
                var unit = length > 0 ? new Vector3f((float)(sum.X / length), (float)(sum.Y / length), (float)(sum.Z / length)) : new Vector3f(0, 0, 1);
                foreach (var k in list) welded[k] = unit;
            }

            var vertices = new float[count * 6];
            for (var k = 0; k < count; k++)
            {
                var p = mesh.Vertices[k];
                vertices[k * 6] = p.X; vertices[k * 6 + 1] = p.Y; vertices[k * 6 + 2] = p.Z;
                vertices[k * 6 + 3] = welded[k].X; vertices[k * 6 + 4] = welded[k].Y; vertices[k * 6 + 5] = welded[k].Z;
            }

            var indices = new List<uint>(mesh.Faces.Count * 6);
            foreach (var face in mesh.Faces)
            {
                indices.Add((uint)face.A); indices.Add((uint)face.B); indices.Add((uint)face.C);
                if (face.IsQuad) { indices.Add((uint)face.A); indices.Add((uint)face.C); indices.Add((uint)face.D); }
            }

            return new Arrays { Vertices = vertices, Indices = indices.ToArray() };
        }
    }

    private sealed record MeshBuffers(uint Vao, uint Vbo, uint Ebo, int Count);

    private sealed class ContextState
    {
        internal readonly uint Program;
        internal readonly int Mvp, ViewportSize, Width, Color;
        internal readonly Dictionary<Mesh, MeshBuffers> Buffers = new(ReferenceEqualityComparer.Instance);

        internal ContextState(Gl gl)
        {
            Program = gl.CreateProgram();
            gl.AttachShader(Program, Compile(gl, Gl.VertexShader, Vertex));
            gl.AttachShader(Program, Compile(gl, Gl.FragmentShader, Fragment));
            gl.LinkProgram(Program);
            var ok = new int[1];
            gl.GetProgramiv(Program, Gl.LinkStatus, ok);
            if (ok[0] == 0) throw new InvalidOperationException("Outline shader did not link.");
            Mvp = gl.GetUniformLocation(Program, "uMVP");
            ViewportSize = gl.GetUniformLocation(Program, "uViewport");
            Width = gl.GetUniformLocation(Program, "uWidth");
            Color = gl.GetUniformLocation(Program, "uColor");
        }

        private static uint Compile(Gl gl, uint kind, string source)
        {
            var shader = gl.CreateShader(kind);
            gl.ShaderSource(shader, 1, new[] { source }, null);
            gl.CompileShader(shader);
            var ok = new int[1];
            gl.GetShaderiv(shader, Gl.CompileStatus, ok);
            if (ok[0] == 0) throw new InvalidOperationException("Outline shader did not compile.");
            return shader;
        }

        internal MeshBuffers BuffersFor(Gl gl, Mesh mesh)
        {
            if (Buffers.TryGetValue(mesh, out var existing)) return existing;
            var arrays = CpuArrays.GetValue(mesh, Arrays.From);
            var ids = new uint[1];
            gl.GenVertexArrays(1, ids);
            var vao = ids[0];
            gl.BindVertexArray(vao);
            gl.GenBuffers(1, ids);
            var vbo = ids[0];
            gl.BindBuffer(Gl.ArrayBuffer, vbo);
            gl.BufferData(Gl.ArrayBuffer, (IntPtr)(arrays.Vertices.Length * 4), arrays.Vertices, Gl.StaticDraw);
            gl.GenBuffers(1, ids);
            var ebo = ids[0];
            gl.BindBuffer(Gl.ElementArrayBuffer, ebo);
            gl.BufferData(Gl.ElementArrayBuffer, (IntPtr)(arrays.Indices.Length * 4), arrays.Indices, Gl.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, Gl.Float, 0, 24, IntPtr.Zero);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, Gl.Float, 0, 24, (IntPtr)12);
            var buffers = new MeshBuffers(vao, vbo, ebo, arrays.Indices.Length);
            Buffers[mesh] = buffers;
            return buffers;
        }

        internal void Release(Gl gl)
        {
            foreach (var b in Buffers.Values)
            {
                gl.DeleteBuffers(2, new[] { b.Vbo, b.Ebo });
                gl.DeleteVertexArrays(1, new[] { b.Vao });
            }

            Buffers.Clear();
        }
    }

    private readonly struct Saved
    {
        private readonly int _program, _vao, _arrayBuffer, _depthFunc, _stencilFunc, _stencilRef, _stencilValueMask;
        private readonly int _stencilFail, _stencilDepthFail, _stencilDepthPass, _stencilWriteMask;
        private readonly bool _depthTest, _stencilTest, _cullFace;
        private readonly byte _depthMask;
        private readonly byte[] _colorMask;

        private Saved(Gl gl)
        {
            int Get(uint name) { var v = new int[4]; gl.glGetIntegerv(name, v); return v[0]; }
            _program = Get(Gl.CurrentProgram);
            _vao = Get(Gl.VertexArrayBinding);
            _arrayBuffer = Get(Gl.ArrayBufferBinding);
            _depthFunc = Get(Gl.DepthFuncName);
            _stencilFunc = Get(Gl.StencilFuncName);
            _stencilRef = Get(Gl.StencilRef);
            _stencilValueMask = Get(Gl.StencilValueMask);
            _stencilFail = Get(Gl.StencilFail);
            _stencilDepthFail = Get(Gl.StencilPassDepthFail);
            _stencilDepthPass = Get(Gl.StencilPassDepthPass);
            _stencilWriteMask = Get(Gl.StencilWriteMask);
            _depthTest = gl.glIsEnabled(Gl.DepthTest) != 0;
            _stencilTest = gl.glIsEnabled(Gl.StencilTest) != 0;
            _cullFace = gl.glIsEnabled(Gl.CullFace) != 0;
            var depthMask = new byte[4];
            gl.glGetBooleanv(Gl.DepthWriteMask, depthMask);
            _depthMask = depthMask[0];
            _colorMask = new byte[4];
            gl.glGetBooleanv(Gl.ColorWriteMask, _colorMask);
        }

        internal static Saved Read(Gl gl) => new(gl);

        internal void Restore(Gl gl)
        {
            gl.UseProgram((uint)_program);
            gl.BindVertexArray((uint)_vao);
            gl.BindBuffer(Gl.ArrayBuffer, (uint)_arrayBuffer);
            void Set(uint cap, bool on) { if (on) gl.glEnable(cap); else gl.glDisable(cap); }
            Set(Gl.DepthTest, _depthTest);
            Set(Gl.StencilTest, _stencilTest);
            Set(Gl.CullFace, _cullFace);
            gl.glDepthMask(_depthMask);
            gl.glColorMask(_colorMask[0], _colorMask[1], _colorMask[2], _colorMask[3]);
            gl.glDepthFunc((uint)_depthFunc);
            gl.glStencilFunc((uint)_stencilFunc, _stencilRef, (uint)_stencilValueMask);
            gl.glStencilOp((uint)_stencilFail, (uint)_stencilDepthFail, (uint)_stencilDepthPass);
            gl.glStencilMask((uint)_stencilWriteMask);
        }
    }

    /// <summary>OpenGL entry points: 1.1 from opengl32.dll, the rest through wglGetProcAddress.</summary>
    private sealed class Gl
    {
        internal const uint Float = 0x1406, UnsignedInt = 0x1405, Triangles = 0x0004;
        internal const uint ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, StaticDraw = 0x88E4;
        internal const uint VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82;
        internal const uint DepthTest = 0x0B71, StencilTest = 0x0B90, CullFace = 0x0B44;
        internal const uint CurrentProgram = 0x8B8D, VertexArrayBinding = 0x85B5, ArrayBufferBinding = 0x8894;
        internal const uint DepthWriteMask = 0x0B72, ColorWriteMask = 0x0C23, DepthFuncName = 0x0B74, Viewport = 0x0BA2;
        internal const uint StencilFuncName = 0x0B92, StencilRef = 0x0B97, StencilValueMask = 0x0B93;
        internal const uint StencilFail = 0x0B94, StencilPassDepthFail = 0x0B95, StencilPassDepthPass = 0x0B96, StencilWriteMask = 0x0B98;
        internal const uint Always = 0x0207, NotEqual = 0x0205, LessEqual = 0x0203, Keep = 0x1E00, Replace = 0x1E01;

        [DllImport("opengl32.dll")] internal static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll", EntryPoint = "wglGetCurrentContext")] private static extern IntPtr CurrentContext();
        [DllImport("opengl32.dll", EntryPoint = "glEnable")] private static extern void Enable(uint cap);
        [DllImport("opengl32.dll", EntryPoint = "glDisable")] private static extern void Disable(uint cap);
        [DllImport("opengl32.dll", EntryPoint = "glIsEnabled")] private static extern byte IsEnabled(uint cap);
        [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")] private static extern void GetIntegerv(uint name, int[] values);
        [DllImport("opengl32.dll", EntryPoint = "glGetBooleanv")] private static extern void GetBooleanv(uint name, byte[] values);
        [DllImport("opengl32.dll", EntryPoint = "glDepthMask")] private static extern void DepthMask(byte flag);
        [DllImport("opengl32.dll", EntryPoint = "glColorMask")] private static extern void ColorMask(byte r, byte g, byte b, byte a);
        [DllImport("opengl32.dll", EntryPoint = "glDepthFunc")] private static extern void DepthFunc(uint func);
        [DllImport("opengl32.dll", EntryPoint = "glStencilFunc")] private static extern void StencilFunc(uint func, int reference, uint mask);
        [DllImport("opengl32.dll", EntryPoint = "glStencilOp")] private static extern void StencilOp(uint fail, uint zfail, uint zpass);
        [DllImport("opengl32.dll", EntryPoint = "glStencilMask")] private static extern void StencilMask(uint mask);
        [DllImport("opengl32.dll", EntryPoint = "glDrawElements")] private static extern void DrawElements(uint mode, int count, uint type, IntPtr indices);

        internal IntPtr wglGetCurrentContext() => CurrentContext();
        internal void glEnable(uint cap) => Enable(cap);
        internal void glDisable(uint cap) => Disable(cap);
        internal byte glIsEnabled(uint cap) => IsEnabled(cap);
        internal void glGetIntegerv(uint name, int[] values) => GetIntegerv(name, values);
        internal void glGetBooleanv(uint name, byte[] values) => GetBooleanv(name, values);
        internal void glDepthMask(byte flag) => DepthMask(flag);
        internal void glColorMask(byte r, byte g, byte b, byte a) => ColorMask(r, g, b, a);
        internal void glDepthFunc(uint func) => DepthFunc(func);
        internal void glStencilFunc(uint func, int reference, uint mask) => StencilFunc(func, reference, mask);
        internal void glStencilOp(uint fail, uint zfail, uint zpass) => StencilOp(fail, zfail, zpass);
        internal void glStencilMask(uint mask) => StencilMask(mask);
        internal void glDrawElements(uint mode, int count, uint type, IntPtr indices) => DrawElements(mode, count, type, indices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint CreateShaderFn(uint kind);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void ShaderSourceFn(uint shader, int count, string[] sources, int[]? lengths);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void UintFn(uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void GetivFn(uint target, uint name, int[] values);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint CreateProgramFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void AttachShaderFn(uint program, uint shader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int UniformLocationFn(uint program, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void UniformMatrixFn(int location, int count, byte transpose, float[] value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Uniform1Fn(int location, float x);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Uniform2Fn(int location, float x, float y);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Uniform4Fn(int location, float x, float y, float z, float w);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void GenFn(int count, uint[] ids);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void BindBufferFn(uint target, uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void BufferFloatsFn(uint target, IntPtr size, float[] data, uint usage);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void BufferUintsFn(uint target, IntPtr size, uint[] data, uint usage);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void AttribPointerFn(uint index, int size, uint type, byte normalized, int stride, IntPtr offset);

        internal readonly CreateShaderFn CreateShader;
        internal readonly ShaderSourceFn ShaderSource;
        internal readonly UintFn CompileShader, LinkProgram, UseProgram, BindVertexArray, EnableVertexAttribArray;
        internal readonly GetivFn GetShaderiv, GetProgramiv;
        internal readonly CreateProgramFn CreateProgram;
        internal readonly AttachShaderFn AttachShader;
        internal readonly UniformLocationFn GetUniformLocation;
        internal readonly UniformMatrixFn UniformMatrix4fv;
        internal readonly Uniform1Fn Uniform1f;
        internal readonly Uniform2Fn Uniform2f;
        internal readonly Uniform4Fn Uniform4f;
        internal readonly GenFn GenBuffers, GenVertexArrays, DeleteBuffers, DeleteVertexArrays;
        internal readonly BindBufferFn BindBuffer;
        private readonly BufferFloatsFn _bufferFloats;
        private readonly BufferUintsFn _bufferUints;
        internal readonly AttribPointerFn VertexAttribPointer;

        internal Gl()
        {
            T Load<T>(string name) where T : Delegate
            {
                var pointer = wglGetProcAddress(name);
                if (pointer == IntPtr.Zero || pointer == (IntPtr)1 || pointer == (IntPtr)2 || pointer == (IntPtr)3 || pointer == (IntPtr)(-1))
                    throw new InvalidOperationException("OpenGL function missing: " + name);
                return Marshal.GetDelegateForFunctionPointer<T>(pointer);
            }

            CreateShader = Load<CreateShaderFn>("glCreateShader");
            ShaderSource = Load<ShaderSourceFn>("glShaderSource");
            CompileShader = Load<UintFn>("glCompileShader");
            LinkProgram = Load<UintFn>("glLinkProgram");
            UseProgram = Load<UintFn>("glUseProgram");
            BindVertexArray = Load<UintFn>("glBindVertexArray");
            EnableVertexAttribArray = Load<UintFn>("glEnableVertexAttribArray");
            GetShaderiv = Load<GetivFn>("glGetShaderiv");
            GetProgramiv = Load<GetivFn>("glGetProgramiv");
            CreateProgram = Load<CreateProgramFn>("glCreateProgram");
            AttachShader = Load<AttachShaderFn>("glAttachShader");
            GetUniformLocation = Load<UniformLocationFn>("glGetUniformLocation");
            UniformMatrix4fv = Load<UniformMatrixFn>("glUniformMatrix4fv");
            Uniform1f = Load<Uniform1Fn>("glUniform1f");
            Uniform2f = Load<Uniform2Fn>("glUniform2f");
            Uniform4f = Load<Uniform4Fn>("glUniform4f");
            GenBuffers = Load<GenFn>("glGenBuffers");
            GenVertexArrays = Load<GenFn>("glGenVertexArrays");
            DeleteBuffers = Load<GenFn>("glDeleteBuffers");
            DeleteVertexArrays = Load<GenFn>("glDeleteVertexArrays");
            BindBuffer = Load<BindBufferFn>("glBindBuffer");
            _bufferFloats = Load<BufferFloatsFn>("glBufferData");
            _bufferUints = Load<BufferUintsFn>("glBufferData");
            VertexAttribPointer = Load<AttribPointerFn>("glVertexAttribPointer");
        }

        internal void BufferData(uint target, IntPtr size, float[] data, uint usage) => _bufferFloats(target, size, data, usage);
        internal void BufferData(uint target, IntPtr size, uint[] data, uint usage) => _bufferUints(target, size, data, usage);
    }
}
