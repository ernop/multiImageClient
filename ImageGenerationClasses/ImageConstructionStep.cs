namespace MultiClientRunner
{
    
    
        /// <summary>
        /// a step along the pipeline of generating the prompt.
        /// The idea is that you generate these as you go along expanding/modifying the prompt, and then at least have them 
        /// to accompany the actual image output.
        /// </summary>
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
