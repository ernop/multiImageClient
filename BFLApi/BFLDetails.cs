namespace IdeogramAPIClient
{
    public class BFLDetails
    {
        private int _width = 1024;
        private int _height = 1024;

        public int Width
        {
            get => _width;
            set
            {
                SetWidth(value);
            }
        }

        public int Height
        {
            get => _height;
            set
            {
                SetHeight(value);
            } 
        }

        public bool PromptUpsampling { get; set; } = false;
        public int SafetyTolerance { get; set; } = 6;
        public int? Seed { get; set; }

        public BFLDetails() { }

        public BFLDetails(BFLDetails other)
        {
            SetWidth(other.Width);
            SetHeight(other.Height);
            PromptUpsampling = other.PromptUpsampling;
            SafetyTolerance = other.SafetyTolerance;
            Seed = other.Seed;
        }

        private void SetWidth(int value)
        {
            if (value % 32 != 0)
            {
                throw new System.ArgumentException("Image width must be a multiple of 32");
            }
            if (value > 1440)
            {
                throw new System.ArgumentException("Image width must be less than or equal to 1440");
            }
            _width = value;
        }

        private void SetHeight(int value)
        {
            if (value % 32 != 0)
            {
                throw new System.ArgumentException("Image height must be a multiple of 32");
            }
            if (value > 1440)
            {
                throw new System.ArgumentException("Image height must be less than or equal to 1440");
            }
            _height = value;
        }
    }
}
