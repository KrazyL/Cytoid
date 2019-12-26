namespace Cytoid.Storyboard.Controllers
{
    public class TapeEaser : StoryboardRendererEaser<ControllerState>
    {
        public TapeEaser()
        {  
            Provider.Tape.Apply(it =>
            {
                it.enabled = false;
            });
        }

        public override void OnUpdate()
        {
            if (Config.UseEffects)
            {
                if (From.Tape.IsSet())
                {
                    Provider.Tape.enabled = From.Tape.Value;
                }
            }
        }
    }
}