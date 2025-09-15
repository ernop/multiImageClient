using MultiImageClient;

namespace IdeogramAPIClient
{
    public class BFL11UltraDetails : IDetails
    {
        //private int _width = 1024;
        //private int _height = 1024;
        private string _aspectRatio = "1:1";
        
        public string AspectRatio
        {
            get => _aspectRatio;
            set
            {
                SetAspectRatio(value);
            }
        }

        public bool PromptUpsampling { get; set; } = true;
        public int SafetyTolerance { get; set; } = 6;
        public int? Seed { get; set; }

        public BFL11UltraDetails() { }

        public BFL11UltraDetails(BFL11UltraDetails other)
        {
            //SetWidth(other.Width);
            //SetHeight(other.Height);
            PromptUpsampling = other.PromptUpsampling;
            SafetyTolerance = other.SafetyTolerance;
            Seed = other.Seed;
        }

        //private void SetWidth(int value)
        //{
        //    if (value % 32 != 0)
        //    {
        //        throw new System.ArgumentException("Image width must be a multiple of 32");
        //    }
        //    if (value > 3900)
        //    {
        //        throw new System.ArgumentException("Image width must be less than or equal to 3900");
        //    }
        //    _width = value;
        //}

        //private void SetHeight(int value)
        //{
        //    if (value % 32 != 0)
        //    {
        //        throw new System.ArgumentException("Image height must be a multiple of 32");
        //    }
        //    if (value > 3900)
        //    {
        //        throw new System.ArgumentException("Image height must be less than or equal to 3900");
        //    }
        //    _height = value;
        //}

        private void SetAspectRatio(string aspectRatio)
        {

            _aspectRatio = aspectRatio;
        }

        public string GetDescription()
        {
            return $"{_aspectRatio}";
        }
    }
}
