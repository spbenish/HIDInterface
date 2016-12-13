# HIDInterface
C# Wrapper of HIDAPI from signal11, wrapper multiplatform, used for interfaction with generic HID Devices, USB or bluetooth

Equim: Trying to wrap more functions.

## How to import C dll
1) You must use:

[DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]

Also look into setting the [CharSet = CharSet.Ansi] when dealing with strings strings.

2) See how the C# types map to C types.
Primitive types map readily!
(int, short, ushort)
The only issue might be that long in C++ is 32-bit, so you should define all such instances as int in your C# code

3) passing strings:
int takes_string(char* s);
int takes_string(const char* s);
int takes_string(const unsigned char* s);
int takes_string(wchar_t* ws);
(and other similar variations...);

For all of them you should use: 
[DLLImport("...")]
private static extern int takes_string([In] string s)

The [In] attribute tells the marshaler that this is passed into a function and not returned.

4) Getting strings back:
int fills_string(char* pBuffer);
int fills_string(wchar_t* pBuffer);

For these ones use:
[DLLImport("...")]
private static extern int fills_string(StringBuilder sb);

Call it like this in c# (use the StringBuilder rather than string class on c# side):

var sb = new StringBuilder(255);
fills_string(sb);
Console.WriteLine(sb.ToString())


You use StringBuilder with char* and string with const char*.

Refer to the table for how C# types map onto C types:
https://msdn.microsoft.com/en-us/library/fzhhdwae(v=vs.110).aspx


Watch out for memory leaks! You can not call free in managed code, for pointers that you get from unmanaged code.
So here is a memory leak:

If the C function is such:

extern "C" __declspec(dllexport) wchar_t* SysGetLibInfo(void)
{
    wchar_t* pStr = (wchar_t*)malloc(...);

    // set all zeros
    memset(pStr, 100, 0);
    // fill with data
    wcscpy(pStr, "This is some info to return");

    return pStr;
}

Then every time you call it from c# it allocates memory, but you as library user can not free this memory.

Auto-marshaling:
This is code without auto-marshaling:
in C++ i have a function:

void GetImage(void* ptr)
{
    CGrabResultPtr ptrGrabResult;
    //here image is grabbing to ptrGrabResult
    camera.GrabOne(5000,ptrGrabResult,TimeoutHandling_ThrowException);
    //i'm copying byte array with image to my pointer ptr
    memcpy(ptr,ptrGrabResult->GetBuffer(),_width*_height);

    if (ptrGrabResult->GrabSucceeded())
        return;
    else
        cout<<endl<<"Grab Failed;"<<endl;
}

and in C#:

[DllImport("libPylonInterface.so")]
private static extern void GetImage(IntPtr ptr);

public static void GetImage(out byte[] arr)
{
    //allocating unmanaged memory for byte array
    IntPtr ptr = Marshal.AllocHGlobal (_width * _height);
    //and "copying" image data to this pointer
    GetImage (ptr);

    arr = new byte[_width * _height];
    //copying from unmanaged to managed memory
    Marshal.Copy (ptr, arr, 0, _width * _height);
    Marshal.FreeHGlobal(ptr);
}

So we have to do many memory manipulations in c#.

Now this is with auto-marshaling:
[DllImport("libPylonInterface.so")]
private static extern void GetImage([Out] byte[] arr);

....

arr = new byte[_width * _height];
GetImage(arr);
// now the arr is filled with data

This also avoids the second memory copy because the marshaller will pin the managed array and pass its address to the unmanaged code. 
The unmanaged code can then populate the managed memory directly.


How to return (! Note: this is only a problem when the function actually returns a char* or wchar_t*) 
a difficult type such as string with auto-marshaling:
For c funcion such as:
const wchar_t * SysGetLibInfo() {
    return dllmanager.SysGetLibInfo();
}

C# is as follows:

[DllImport(dll, CallingConvention = CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(MyMarshaller))]
private static extern string SysGetLibInfo();

Then you must implement your own CustomMarshaler class which implements ICustomMarshaler interface.


Manually managing memory in C#:
If for example you must pass a double** to the C function. One way to handle that is to manually allocate and marshal unmanaged memory. 
You cannot use a double[,] because that simply does not map to double*[].
Then in C# code you will have things like this (to setup double** shich is to be passed to a C function):

IntPtr[] CreateUnmanagedArrays(double[][] arr)
{
    IntPtr[] result = new IntPtr[arr.Length];
    for (int i=0; i<arr.Length; i++)
    {
        result[i] = Marshal.AllocCoTaskMem(arr[i].Length*sizeof(double));
        Marshal.Copy(arr[i], 0, result[i], arr[i].Length);
    }
    return result;
}

void DestroyUnmanagedArrays(IntPtr[] arr)
{
    for (int i=0; i<arr.Length; i++)
    {
        Marshal.FreeCoTaskMem(arr[i]);
        arr[i] = IntPtr.Zero;
    }
}


When things get too complex consider:
1) Stop this auto-marshalling stuff, start using IntPtr and use Marshal class to move memory around yourself.
2) Use unsafe blocks.


Example with no auto-marshalling:

To call this C function:
char * GetDir(char* path ) {
    // whatever
    return path;
}

You use in C#:
[DllImport("your.dll", CharSet = CharSet.Ansi)]
IntPtr GetDir(StringBuilder path);

And then when you get the IntPtr you pass it to Marshal.PtrToStringAnsi() function in order to get a C# string back.

Example with unsafe blocks:
The C / C++ code:

int API_ReadFile(const wchar_t* filename, DataStruct** outData)
{
    *outData = new DataStruct();
    (*outData)->data = (unsigned char*)_strdup("hello");
    (*outData)->len = 5;
    return 0;
}

void API_Free(DataStruct** pp)
{
    free((*pp)->data);
    delete *pp;
    *pp = NULL;
}

The C# code to access those functions are as follows:

[StructLayout(LayoutKind.Sequential)]
struct DataStruct
{
    public IntPtr data;
    public int len;
};

[DllImport("ReadFile.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
unsafe private static extern int API_ReadFile([MarshalAs(UnmanagedType.LPWStr)]string filename, DataStruct** outData);

[DllImport("ReadFile.dll", CallingConvention = CallingConvention.Cdecl)]
unsafe private static extern void API_Free(DataStruct** handle);

unsafe static int ReadFile(string filename, out byte[] buffer)
{
    DataStruct* outData;
    int result = API_ReadFile(filename, &outData);
    buffer = new byte[outData->len];
    Marshal.Copy((IntPtr)outData->data, buffer, 0, outData->len);
    API_Free(&outData);
    return result;
}

static void Main(string[] args)
{
    byte[] buffer;
    ReadFile("test.txt", out buffer);
    foreach (byte ch in buffer)
    {
        Console.Write("{0} ", ch);
    }
    Console.Write("\n");
}



Whenever you can just use byte[] arrays for marshalling, otherwise you run into all sorts of troubles such as:
The C code:

typedef struct s_parameterStuct
{
    int count;
    char name[ 128 ];
} parameterStruct;

And the c# code:

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public class parameterStuct
{
    public int count;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public char[] name;
}

But since char is 2 bytes in c# the SizeConst for name should be 64 (becasue 64 of these occupy 128 bytes).



Further links:
http://stackoverflow.com/questions/17620396/tutorial-needed-on-invoking-unmanaged-dll-from-c-net
