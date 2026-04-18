using Mirror;

namespace DrivingRangeTheater
{
    // Client → server: request to cycle the video index. Direction is +1 (next) or -1 (back).
    public struct TheaterCycleMsg : NetworkMessage
    {
        public int direction;
    }
}
