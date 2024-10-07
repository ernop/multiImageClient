namespace MultiClientRunner
{
        /// a step along the pipeline of generating the prompt.
        /// The idea is that you generate these as you go along expanding/modifying the prompt, 
        /// and then at least have them to accompany the actual image output to know its history.
        public class ImageConstructionStep
        {
            public ImageConstructionStep(string description, string details)
            {
                Description = description;
                Details = details;
            }
            public string Description { get; set; }
            public string Details { get; set; }
        }
    }
