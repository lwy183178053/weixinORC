using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Client.Controls
{
    public sealed class HorizontalResizeSplitter : Grid
    {
        public HorizontalResizeSplitter()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        }
    }
}
