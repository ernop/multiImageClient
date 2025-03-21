﻿using IdeogramAPIClient;
using OpenAI.Images;

namespace MultiImageClient
{
    public class Dalle3Details : IDetails
    {
        public string Model { get; set; }
        public GeneratedImageSize Size { get; set; }
        public GeneratedImageQuality Quality { get; set; }
        public GeneratedImageFormat Format { get; set; }

        public Dalle3Details() { }
        public Dalle3Details(Dalle3Details other)
        {
            Model = other.Model;
            Size = other.Size;
            Quality = other.Quality;
            Format = other.Format;
        }

        public string GetDescription()
        {
            return $"{Model}-{Size}-{Quality}-{Format}";
        }
    }
}